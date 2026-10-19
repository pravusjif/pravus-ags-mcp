//=============================================================================
// agsmcp.dll - AGS engine plugin that lets the editor plugin (AGS.Plugin.Mcp)
// drive the running game: read state, take screenshots and simulate input.
//
// It is INERT unless the AGSMCP_PORT environment variable is set, which only
// the editor plugin's launcher does, so a shipped game is unaffected.
//
// A worker thread runs a loopback TCP server (line-delimited JSON). It only
// queues commands; the queue is drained on the game's main thread from
// AGSE_PRERENDER so that all engine access happens on the thread AGS expects.
//=============================================================================
#define _CRT_SECURE_NO_WARNINGS
#define THIS_IS_THE_PLUGIN
#define NOMINMAX

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "agsplugin.h"
#include "json_min.h"

#include <algorithm>
#include <string>
#include <deque>
#include <vector>
#include <memory>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <cstdint>

#pragma comment(lib, "ws2_32.lib")

static IAGSEngine* engine = nullptr;

// ---- script function pointers, resolved lazily on the main thread ----------
typedef void (__cdecl *fn_simkey_t)(int);
typedef int  (__cdecl *fn_savescreenshot_t)(const char*);
typedef void (__cdecl *fn_setint_t)(int);
typedef int  (__cdecl *fn_getglobalint_t)(int);
typedef void (__cdecl *fn_setglobalint_t)(int, int);
typedef int  (__cdecl *fn_isinterfaceenabled_t)();
typedef void (__cdecl *fn_getlocationname_t)(int, int, char*);

static fn_simkey_t            fn_simkey = nullptr;
static fn_savescreenshot_t    fn_savescreenshot = nullptr;
static fn_setint_t            fn_setcursormode = nullptr;
static fn_setint_t            fn_setactiveinv = nullptr;
static fn_getglobalint_t      fn_getglobalint = nullptr;
static fn_setglobalint_t      fn_setglobalint = nullptr;
static fn_isinterfaceenabled_t fn_isinterfaceenabled = nullptr;
static fn_getlocationname_t   fn_getlocationname = nullptr;
static bool g_resolved = false;

// ---- command queue (worker thread -> main thread) --------------------------
struct Request {
    std::string cmd;
    jm::Value args;
    jm::Value result;
    std::mutex m;
    std::condition_variable cv;
    bool done = false;
};

static std::mutex g_qmtx;
static std::deque<std::shared_ptr<Request> > g_queue;
// The request the main thread is handling (under g_qmtx). A command can quit the game from inside its handler
// (call_function runs a QuitGame at once), so AGS_EngineShutdown must answer it: it will never return.
static std::shared_ptr<Request> g_current;

// ---- event log (main thread only) ------------------------------------------
static std::deque<std::string> g_events;

// ---- server ----------------------------------------------------------------
// g_listen and g_client are closed from two threads (the worker when a client leaves, AGS_EngineShutdown on the
// main thread), so both are only touched under g_sock_mtx.
static std::atomic<bool> g_server_running(false);
static std::atomic<bool> g_worker_done(false);
static std::mutex g_sock_mtx;
static SOCKET g_listen = INVALID_SOCKET;
static SOCKET g_client = INVALID_SOCKET;
static std::thread g_worker;
static int g_port = 0;
static int g_shot_counter = 0; // main thread only

static void log_info(const char* msg) {
    if (engine) engine->Log(AGSLOG_LEVEL_INFO, "agsmcp: %s", msg);
}

static std::string safe_str(const char* p, size_t cap) {
    if (!p) return std::string();
    size_t n = 0;
    while (n < cap && p[n] != 0) ++n;
    return std::string(p, n);
}

static int argi(const jm::Value& args, const char* k, int def = 0) {
    if (args.type != jm::Type::Obj) return def;
    const jm::Value* v = args.find(k);
    return v ? (int)v->as_int(def) : def;
}

// ----------------------------------------------------- main-thread handlers

// The engine stores views 0-based with a negative "none" (an object's 0xFFFF reads as -1 through the
// plugin's short). Report them as the editor and script do (Character.View, Object.View): 1-based, 0 = none.
static int editor_view(int view) { return view < 0 ? 0 : view + 1; }

static jm::Value character_json(int id, AGSCharacter* c, bool withInv) {
    jm::Value j = jm::Value::MkObj();
    j.set("id", id);
    j.set("scriptName", safe_str(c->scrname, sizeof(c->scrname)));
    j.set("name", safe_str(c->name, sizeof(c->name)));
    j.set("room", c->room);
    j.set("x", c->x);
    j.set("y", c->y);
    j.set("view", editor_view(c->view));
    j.set("loop", (int)c->loop);
    j.set("frame", (int)c->frame);
    j.set("walking", (bool)(c->walking != 0));
    j.set("animating", (bool)(c->animating != 0));
    j.set("on", (int)(unsigned char)c->on);
    if (withInv) {
        j.set("activeInv", c->activeinv);
        jm::Value inv = jm::Value::MkArr();
        for (int it = 1; it < 301; ++it) {
            if (c->inv[it] > 0) {
                jm::Value e = jm::Value::MkObj();
                e.set("item", it);
                e.set("count", (int)c->inv[it]);
                inv.arr.push_back(e);
            }
        }
        j.set("inventory", inv);
    }
    return j;
}

static jm::Value cmd_state() {
    jm::Value r = jm::Value::MkObj();
    r.set("ok", true);
    r.set("room", engine->GetCurrentRoom());
    r.set("paused", (int)engine->IsGamePaused());
    if (fn_isinterfaceenabled) r.set("interfaceEnabled", (int)fn_isinterfaceenabled());

    int mx = 0, my = 0;
    engine->GetMousePosition(&mx, &my);
    jm::Value mouse = jm::Value::MkObj();
    mouse.set("x", mx);
    mouse.set("y", my);
    r.set("mouse", mouse);

    AGSGameOptions* go = engine->GetGameOptions();
    if (go) {
        jm::Value o = jm::Value::MkObj();
        o.set("score", go->score);
        o.set("disabledUserInterface", go->disabled_user_interface);
        o.set("inCutscene", go->in_cutscene);
        o.set("fastForward", go->fast_forward);
        o.set("roomWidth", go->room_width);
        o.set("roomHeight", go->room_height);
        r.set("options", o);
    }

    int pc = engine->GetPlayerCharacter();
    AGSCharacter* p = engine->GetCharacter(pc);
    if (p) r.set("player", character_json(pc, p, true));

    int nchars = engine->GetNumCharacters();
    jm::Value chars = jm::Value::MkArr();
    for (int i = 0; i < nchars; ++i) {
        AGSCharacter* c = engine->GetCharacter(i);
        if (c) chars.arr.push_back(character_json(i, c, false));
    }
    r.set("characters", chars);

    int nobj = engine->GetNumObjects();
    jm::Value objs = jm::Value::MkArr();
    for (int i = 0; i < nobj; ++i) {
        AGSObject* o = engine->GetObject(i);
        if (!o) continue;
        jm::Value j = jm::Value::MkObj();
        j.set("id", i);
        j.set("x", o->x);
        j.set("y", o->y);
        j.set("sprite", (int)o->num);
        j.set("baseline", (int)o->baseline);
        j.set("visible", (int)(unsigned char)o->on);
        j.set("moving", (int)o->moving);
        j.set("view", editor_view(o->view));
        j.set("loop", (int)o->loop);
        j.set("frame", (int)o->frame);
        objs.arr.push_back(j);
    }
    r.set("objects", objs);

    jm::Value evs = jm::Value::MkArr();
    for (size_t n = 0; n < g_events.size(); ++n) evs.arr.push_back(jm::Value::MkStr(g_events[n]));
    r.set("recentEvents", evs);
    return r;
}

// Post a key down/up pair to the game window so SDL sees it as a real key press. SDL reads the scan code from
// lParam bits 16-23 and the extended-key flag from bit 24: without that flag the arrows, Home/End, PgUp/PgDn and
// Ins/Del share their scan codes with the numeric keypad and arrive as keypad keys (digits with NumLock on).
static bool post_window_key(int vk, bool extended) {
    HWND hwnd = engine->GetWindowHandle();
    if (!hwnd) return false;
    UINT scan = MapVirtualKeyA(vk, MAPVK_VK_TO_VSC);
    LPARAM down = 1 | ((LPARAM)scan << 16);
    if (extended) down |= (LPARAM)1 << 24;
    PostMessageA(hwnd, WM_KEYDOWN, vk, down);
    PostMessageA(hwnd, WM_KEYUP, vk, down | ((LPARAM)1 << 30) | ((LPARAM)1 << 31));
    return true;
}

// AGS key codes that go to the window as real keys instead of Game.SimulateKeyPress. That maps 1..26 to
// Ctrl+A..Z (so Backspace/Tab/Enter arrive as Ctrl+H/I/M) and Home..Delete to the keypad scan codes first
// (ags_key_to_sdl_scan), which NumLock turns into digits. Keep in step with KeyMap.IsWindowRouted (C#).
struct WindowKey { int code; int vk; bool extended; };
static const WindowKey kWindowKeys[] = {
    { 8, VK_BACK, false }, { 9, VK_TAB, false }, { 13, VK_RETURN, false },
    { 359, VK_F1, false }, { 360, VK_F2, false }, { 361, VK_F3, false }, { 362, VK_F4, false }, { 363, VK_F5, false },
    { 364, VK_F6, false }, { 365, VK_F7, false }, { 366, VK_F8, false }, { 367, VK_F9, false }, { 368, VK_F10, false },
    { 433, VK_F11, false }, { 434, VK_F12, false },
    { 371, VK_HOME, true }, { 372, VK_UP, true }, { 373, VK_PRIOR, true }, { 375, VK_LEFT, true },
    { 377, VK_RIGHT, true }, { 379, VK_END, true }, { 380, VK_DOWN, true }, { 381, VK_NEXT, true },
    { 382, VK_INSERT, true }, { 383, VK_DELETE, true },
};

static const WindowKey* find_window_key(int code) {
    for (const WindowKey& k : kWindowKeys)
        if (k.code == code) return &k;
    return nullptr;
}

// Decode UTF-8 into UTF-16 units (invalid bytes become U+FFFD).
static std::vector<wchar_t> utf8_to_utf16(const std::string& s) {
    std::vector<wchar_t> out;
    if (s.empty()) return out;
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    if (n <= 0) return out;
    out.resize((size_t)n);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), out.data(), n);
    return out;
}

// Save one screenshot as $SAVEGAMEDIR$/<name>.bmp. Returns SaveScreenShot's result; 'path' gets the resolved file.
static int save_shot(const std::string& name, std::string& path) {
    std::string scriptPath = "$SAVEGAMEDIR$/" + name + ".bmp";
    int res = fn_savescreenshot(scriptPath.c_str());
    char buf[1024];
    buf[0] = 0;
    engine->ResolveFilePath(scriptPath.c_str(), buf, sizeof(buf));
    path = buf;
    return res;
}

static jm::Value cmd_screenshot() {
    jm::Value r = jm::Value::MkObj();
    if (!fn_savescreenshot) {
        r.set("ok", false);
        r.set("error", "SaveScreenShot is not available");
        return r;
    }
    // A fresh name per shot: a failed save can never hand back the previous picture. The editor side deletes it.
    std::string path;
    int res = save_shot("agsmcp-" + std::to_string((long long)++g_shot_counter), path);
    r.set("ok", res != 0 && !path.empty());
    if (res == 0) r.set("error", "SaveScreenShot failed");
    r.set("result", res);
    r.set("path", path);
    return r;
}

// ---- screenshot bursts (main thread only) -----------------------------------
// A burst request is not answered by drain_queue: it stays here while PRERENDER captures one frame every 'everyN'
// frames, and is completed when the last frame is saved (or when the game quits). One burst at a time.
struct Burst {
    std::shared_ptr<Request> req;
    int frames = 0, everyN = 1, taken = 0, tick = 0;
    long long id = 0;
    std::chrono::steady_clock::time_point start;
    jm::Value shots;
};
static Burst g_burst;
static const int kMaxBurstFrames = 1000;

static void complete_request(const std::shared_ptr<Request>& req, const jm::Value& result);

static void finish_burst(bool ok, const std::string& error) {
    jm::Value r = jm::Value::MkObj();
    r.set("ok", ok);
    if (!ok) r.set("error", error);
    r.set("everyN", g_burst.everyN);
    r.set("elapsedMs", (int)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - g_burst.start).count());
    r.set("frames", g_burst.shots); // on failure too, so the editor can delete what was written
    std::shared_ptr<Request> req = g_burst.req;
    g_burst = Burst();
    complete_request(req, r);
}

static void capture_burst_frame() {
    std::string path;
    int i = g_burst.taken;
    int res = save_shot("agsmcp-" + std::to_string(g_burst.id) + "-" + std::to_string((long long)i), path);
    if (res == 0 || path.empty()) { finish_burst(false, "SaveScreenShot failed at frame " + std::to_string((long long)i)); return; }
    jm::Value s = jm::Value::MkObj();
    s.set("index", i);
    s.set("path", path);
    s.set("ms", (int)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - g_burst.start).count());
    g_burst.shots.arr.push_back(s);
    if (++g_burst.taken >= g_burst.frames) finish_burst(true, "");
}

// Start a burst for 'req' and capture its first frame now.
static void start_burst(const std::shared_ptr<Request>& req) {
    jm::Value err = jm::Value::MkObj();
    err.set("ok", false);
    if (!fn_savescreenshot) { err.set("error", "SaveScreenShot is not available"); complete_request(req, err); return; }
    if (g_burst.req) { err.set("error", "a screenshot burst is already running"); complete_request(req, err); return; }
    g_burst.req = req;
    g_burst.frames = std::min(argi(req->args, "frames", 1), kMaxBurstFrames);
    g_burst.everyN = std::max(1, argi(req->args, "everyN", 1));
    g_burst.id = ++g_shot_counter;
    g_burst.start = std::chrono::steady_clock::now();
    g_burst.shots = jm::Value::MkArr();
    capture_burst_frame();
}

// Called once per PRERENDER before the queue is drained, so a burst started this frame is not ticked twice.
static void tick_burst() {
    if (!g_burst.req) return;
    if (++g_burst.tick % g_burst.everyN == 0) capture_burst_frame();
}

static bool is_burst(const std::string& cmd, const jm::Value& args) {
    return cmd == "screenshot" && argi(args, "frames", 1) > 1;
}

static jm::Value handle_command(const std::string& cmd, const jm::Value& args) {
    jm::Value r = jm::Value::MkObj();
    if (!engine) {
        r.set("ok", false);
        r.set("error", "engine not ready");
        return r;
    }

    if (cmd == "ping") {
        r.set("ok", true);
        r.set("name", "agsmcp");
        r.set("apiVersion", (int)engine->version);
        const char* ev = engine->GetEngineVersion();
        r.set("engineVersion", std::string(ev ? ev : ""));
        return r;
    }
    if (cmd == "state") return cmd_state();
    if (cmd == "screenshot") return cmd_screenshot();

    if (cmd == "click") {
        engine->SetMousePosition(argi(args, "x"), argi(args, "y"));
        engine->SimulateMouseClick(argi(args, "button", 1));
        r.set("ok", true);
        return r;
    }
    if (cmd == "process_click") {
        // Do NOT call Room.ProcessClick here: we run inside the AGSE_PRERENDER hook, and an interaction handler
        // that blocks (Display, Say, Walk eBlock) would run a nested game loop inside the render callback and
        // wedge the game. Instead queue a real click in the requested mode, so the game's on_mouse_click runs
        // the interaction in normal script context on the next frame.
        if (!fn_setcursormode) { r.set("ok", false); r.set("error", "SetCursorMode is not available"); return r; }
        int inv = argi(args, "inventory", 0);
        if (inv > 0) {
            if (!fn_setactiveinv) { r.set("ok", false); r.set("error", "SetActiveInventory is not available"); return r; }
            fn_setactiveinv(inv);
        }
        fn_setcursormode(argi(args, "mode", 0));
        int32 x = argi(args, "x"), y = argi(args, "y");
        engine->RoomToViewport(&x, &y);
        engine->SetMousePosition(x, y);
        engine->SimulateMouseClick(1);
        r.set("ok", true);
        r.set("queued", true);
        return r;
    }
    if (cmd == "key") {
        int code = argi(args, "code");
        const WindowKey* wk = find_window_key(code);
        if (wk && post_window_key(wk->vk, wk->extended)) {
            r.set("ok", true);
            r.set("via", "window");
            return r;
        }
        if (!fn_simkey) { r.set("ok", false); r.set("error", "SimulateKeyPress is not available"); return r; }
        fn_simkey(code);
        r.set("ok", true);
        r.set("via", "SimulateKeyPress");
        return r;
    }
    if (cmd == "type") {
        // Text for GUI text boxes and on_text_input: WM_CHAR per UTF-16 unit, which SDL turns into text input.
        std::string text;
        if (args.type == jm::Type::Obj) {
            const jm::Value* tv = args.find("text");
            if (tv) text = tv->as_str();
        }
        HWND hwnd = engine->GetWindowHandle();
        if (!hwnd) { r.set("ok", false); r.set("error", "the game window is not available"); return r; }
        std::vector<wchar_t> units = utf8_to_utf16(text);
        for (wchar_t u : units) PostMessageW(hwnd, WM_CHAR, (WPARAM)u, 1);
        r.set("ok", true);
        r.set("units", (int)units.size());
        return r;
    }
    if (cmd == "hover_name") {
        // GetLocationName takes screen coordinates; room=1 converts room coordinates first (as process_click does).
        int32 x = argi(args, "x"), y = argi(args, "y");
        if (argi(args, "room", 0)) engine->RoomToViewport(&x, &y);
        char buf[256];
        buf[0] = 0;
        if (fn_getlocationname) fn_getlocationname(x, y, buf);
        r.set("ok", true);
        r.set("name", safe_str(buf, sizeof(buf)));
        r.set("screenX", (int)x);
        r.set("screenY", (int)y);
        return r;
    }
    if (cmd == "text_extent") {
        // One line in the game's text encoding: 'bytes' (codepage bytes for a non-Unicode game) or 'text' (UTF-8).
        int font = argi(args, "font", 0);
        if (engine->GetFontType(font) == FNT_INVALID) {
            r.set("ok", false);
            r.set("error", "no font " + std::to_string((long long)font) + " in the running game");
            return r;
        }
        std::string text;
        if (args.type == jm::Type::Obj) {
            const jm::Value* bv = args.find("bytes");
            const jm::Value* tv = args.find("text");
            if (bv && bv->type == jm::Type::Arr) {
                for (const jm::Value& b : bv->arr) text.push_back((char)(unsigned char)b.as_int());
            } else if (tv) {
                text = tv->as_str();
            }
        }
        int32 w = 0, h = 0;
        engine->GetTextExtent(font, text.c_str(), &w, &h);
        r.set("ok", true);
        r.set("width", (int)w);
        r.set("height", (int)h);
        return r;
    }
    if (cmd == "get_global_int") {
        int v = fn_getglobalint ? fn_getglobalint(argi(args, "index")) : 0;
        r.set("ok", true);
        r.set("value", v);
        return r;
    }
    if (cmd == "set_global_int") {
        if (fn_setglobalint) fn_setglobalint(argi(args, "index"), argi(args, "value"));
        r.set("ok", (bool)(fn_setglobalint != nullptr));
        return r;
    }
    if (cmd == "call_function") {
        std::string name;
        if (args.type == jm::Type::Obj) {
            const jm::Value* nv = args.find("name");
            if (nv) name = nv->as_str();
        }
        if (name.empty()) { r.set("ok", false); r.set("error", "call_function requires a 'name'"); return r; }
        intptr_t a1 = 0, a2 = 0;
        int n = 0;
        if (args.type == jm::Type::Obj) {
            const jm::Value* a = args.find("args");
            if (a && a->type == jm::Type::Arr) {
                n = (int)a->arr.size();
                if (n >= 1) a1 = (intptr_t)a->arr[0].as_int();
                if (n >= 2) a2 = (intptr_t)a->arr[1].as_int();
                if (n > 2) n = 2;
            }
        }
        // globalScript 1 = GlobalScript.asc, 0 = the current room's script.
        int global = argi(args, "globalScript", 1) ? 1 : 0;
        engine->QueueGameScriptFunction(name.c_str(), global, n, a1, a2);
        r.set("ok", true);
        r.set("queued", true);
        r.set("globalScript", global != 0);
        return r;
    }

    r.set("ok", false);
    r.set("error", std::string("unknown command: ") + cmd);
    return r;
}

static void resolve_functions() {
    if (g_resolved || !engine) return;
    fn_simkey = (fn_simkey_t)engine->GetScriptFunctionAddress("Game::SimulateKeyPress");
    fn_savescreenshot = (fn_savescreenshot_t)engine->GetScriptFunctionAddress("SaveScreenShot");
    fn_setcursormode = (fn_setint_t)engine->GetScriptFunctionAddress("SetCursorMode");
    fn_setactiveinv = (fn_setint_t)engine->GetScriptFunctionAddress("SetActiveInventory");
    fn_getglobalint = (fn_getglobalint_t)engine->GetScriptFunctionAddress("GetGlobalInt");
    fn_setglobalint = (fn_setglobalint_t)engine->GetScriptFunctionAddress("SetGlobalInt");
    fn_isinterfaceenabled = (fn_isinterfaceenabled_t)engine->GetScriptFunctionAddress("IsInterfaceEnabled");
    fn_getlocationname = (fn_getlocationname_t)engine->GetScriptFunctionAddress("GetLocationName");
    // Treat the symbol table as ready once the common ones resolve; retry next frame otherwise.
    if (fn_simkey && fn_getglobalint) {
        g_resolved = true;
        log_info("script functions resolved");
    }
}

static void complete_request(const std::shared_ptr<Request>& req, const jm::Value& result) {
    {
        std::lock_guard<std::mutex> lk(req->m);
        if (req->done) return;
        req->result = result;
        req->done = true;
    }
    req->cv.notify_all();
}

// Drain the command queue on the game main thread.
static void drain_queue() {
    resolve_functions();
    tick_burst();
    for (;;) {
        std::shared_ptr<Request> req;
        {
            std::lock_guard<std::mutex> lk(g_qmtx);
            if (g_queue.empty()) break;
            req = g_queue.front();
            g_queue.pop_front();
            if (!is_burst(req->cmd, req->args)) g_current = req;
        }
        if (is_burst(req->cmd, req->args)) { start_burst(req); continue; } // answered by finish_burst
        jm::Value result = handle_command(req->cmd, req->args);
        {
            std::lock_guard<std::mutex> lk(g_qmtx);
            g_current.reset();
        }
        complete_request(req, result);
    }
}

static jm::Value shutting_down_result() {
    jm::Value r = jm::Value::MkObj();
    r.set("ok", false);
    r.set("error", "shutting down");
    return r;
}

// Answer every outstanding request at once (the game is quitting, so PRERENDER will not drain them again): the one
// being handled ran and caused the quit, the queued ones never run.
static void fail_outstanding_requests() {
    std::deque<std::shared_ptr<Request> > pending;
    std::shared_ptr<Request> current;
    {
        std::lock_guard<std::mutex> lk(g_qmtx);
        pending.swap(g_queue);
        current.swap(g_current);
    }
    if (current) {
        jm::Value r = jm::Value::MkObj();
        r.set("ok", true);
        r.set("gameExiting", true);
        complete_request(current, r);
    }
    for (const std::shared_ptr<Request>& req : pending) complete_request(req, shutting_down_result());
    if (g_burst.req) finish_burst(false, "the game quit during the burst"); // main thread, like PRERENDER
}

// Enqueue a command and wait for the main thread to process it.
static jm::Value call_on_main(const std::string& cmd, const jm::Value& args) {
    std::shared_ptr<Request> req = std::make_shared<Request>();
    req->cmd = cmd;
    req->args = args;
    {
        // Checked under the queue lock: AGS_EngineShutdown clears the flag under it before failing the queue,
        // so a request can never be queued after that and wait out the timeout.
        std::lock_guard<std::mutex> lk(g_qmtx);
        if (!g_server_running) return shutting_down_result();
        g_queue.push_back(req);
    }
    // A burst spans frames: allow 100 ms per frame it waits for, plus the usual margin.
    long long waitMs = 10000;
    if (is_burst(cmd, args))
        waitMs = (long long)std::min(argi(args, "frames", 1), kMaxBurstFrames) * std::max(1, argi(args, "everyN", 1)) * 100 + 5000;
    std::unique_lock<std::mutex> lk(req->m);
    if (!req->cv.wait_for(lk, std::chrono::milliseconds(waitMs), [&] { return req->done; })) {
        jm::Value r = jm::Value::MkObj();
        r.set("ok", false);
        r.set("error", "timed out waiting for the game main thread (is the game blocked?)");
        return r;
    }
    return req->result;
}

static std::string dispatch_line(const std::string& line) {
    jm::Value req;
    jm::Value resp = jm::Value::MkObj();
    if (!jm::parse(line, req) || req.type != jm::Type::Obj) {
        resp.set("ok", false);
        resp.set("error", "invalid JSON request");
        return jm::dump(resp);
    }
    const jm::Value* idv = req.find("id");
    const jm::Value* cmdv = req.find("cmd");
    std::string cmd = cmdv ? cmdv->as_str() : std::string();
    jm::Value args;
    const jm::Value* av = req.find("args");
    if (av) args = *av;

    jm::Value result = call_on_main(cmd, args);
    // Flatten the result object and attach the request id.
    if (idv) result.obj.insert(result.obj.begin(), std::make_pair(std::string("id"), *idv));
    return jm::dump(result);
}

static bool send_all(SOCKET s, const std::string& data) {
    size_t sent = 0;
    while (sent < data.size()) {
        int n = send(s, data.data() + sent, (int)(data.size() - sent), 0);
        if (n <= 0) return false;
        sent += (size_t)n;
    }
    return true;
}

static void handle_client(SOCKET c) {
    // A receive timeout lets the loop notice shutdown even when the client keeps an idle connection open.
    DWORD timeout_ms = 500;
    setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout_ms, sizeof(timeout_ms));
    std::string buf;
    char tmp[4096];
    while (g_server_running) {
        int n = recv(c, tmp, sizeof(tmp), 0);
        if (n == SOCKET_ERROR && WSAGetLastError() == WSAETIMEDOUT) continue;
        if (n <= 0) break;
        buf.append(tmp, (size_t)n);
        size_t pos;
        while ((pos = buf.find('\n')) != std::string::npos) {
            std::string line = buf.substr(0, pos);
            buf.erase(0, pos + 1);
            if (!line.empty() && line[line.size() - 1] == '\r') line.erase(line.size() - 1);
            if (line.empty()) continue;
            std::string resp = dispatch_line(line);
            resp.push_back('\n');
            if (!send_all(c, resp)) return;
        }
    }
}

static void server_loop();

static void server_main() {
    server_loop();
    g_worker_done = true;
}

static void server_loop() {
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return;

    SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) { WSACleanup(); return; }

    BOOL reuse = TRUE;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse));

    sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons((unsigned short)g_port);
    inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

    if (bind(listener, (sockaddr*)&addr, sizeof(addr)) != 0 || listen(listener, 4) != 0) {
        closesocket(listener);
        WSACleanup();
        log_info("failed to bind the loopback server socket");
        return;
    }
    {
        // Published under the lock so that a shutdown racing the start-up either sees it or stops it here.
        std::lock_guard<std::mutex> lk(g_sock_mtx);
        if (!g_server_running) { closesocket(listener); WSACleanup(); return; }
        g_listen = listener;
    }
    log_info("MCP engine bridge listening");

    // accept() runs without the lock; shutdown closes g_listen to unblock it
    while (g_server_running) {
        SOCKET c = accept(listener, 0, 0);
        if (c == INVALID_SOCKET) break;
        {
            std::lock_guard<std::mutex> lk(g_sock_mtx);
            if (!g_server_running) { closesocket(c); break; }
            g_client = c;
        }
        handle_client(c);
        {
            std::lock_guard<std::mutex> lk(g_sock_mtx);
            if (g_client == c) { closesocket(c); g_client = INVALID_SOCKET; } // else shutdown closed it
        }
    }

    {
        std::lock_guard<std::mutex> lk(g_sock_mtx);
        if (g_listen != INVALID_SOCKET) { closesocket(g_listen); g_listen = INVALID_SOCKET; }
    }
    WSACleanup();
}

static void push_event(const std::string& e) {
    g_events.push_back(e);
    while (g_events.size() > 30) g_events.pop_front();
}

// ------------------------------------------------------------- plugin exports

DLLEXPORT const char* AGS_GetPluginName() {
    return "Pravus AGS MCP Engine Bridge";
}

DLLEXPORT int AGS_EditorStartup(IAGSEditor* editor) {
    // Nothing to do in the editor; returning 0 makes the plugin available to games.
    return 0;
}

DLLEXPORT void AGS_EditorShutdown() {
}

DLLEXPORT int AGS_PluginV2() {
    return 1;
}

DLLEXPORT void AGS_EngineStartup(IAGSEngine* lpEngine) {
    engine = lpEngine;

    const char* portStr = getenv("AGSMCP_PORT");
    if (!portStr || !*portStr) {
        // Inert: the game was not launched by the MCP editor plugin.
        return;
    }
    g_port = atoi(portStr);
    if (g_port <= 0 || g_port > 65535) return;

    engine->RequestEventHook(AGSE_PRERENDER);
    engine->RequestEventHook(AGSE_ENTERROOM);
    engine->RequestEventHook(AGSE_LEAVEROOM);
    engine->RequestEventHook(AGSE_POSTRESTOREGAME);

    g_server_running = true;
    g_worker = std::thread(server_main);
}

// Called on the main thread while the game quits. The worker may be blocked in accept(), in recv() on the
// editor's persistent connection, or waiting for a queued request that PRERENDER will never drain again; each
// is released here, so the join (and the game's exit) does not wait on the editor.
DLLEXPORT void AGS_EngineShutdown() {
    if (!g_worker.joinable()) return; // inert, or never started
    {
        std::lock_guard<std::mutex> lk(g_qmtx);
        g_server_running = false;
    }
    {
        std::lock_guard<std::mutex> lk(g_sock_mtx);
        if (g_listen != INVALID_SOCKET) {
            closesocket(g_listen); // unblocks accept()
            g_listen = INVALID_SOCKET;
        }
    }
    fail_outstanding_requests();
    // Let the worker send those answers and leave by itself: its recv() wakes within the 500 ms receive timeout.
    // If it has not gone after 2 s (stuck in send to a client that does not read), cut the connection.
    for (int waited = 0; !g_worker_done && waited < 2000; waited += 10)
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    if (!g_worker_done) {
        std::lock_guard<std::mutex> lk(g_sock_mtx);
        if (g_client != INVALID_SOCKET) {
            shutdown(g_client, SD_BOTH);
            closesocket(g_client);
            g_client = INVALID_SOCKET;
        }
    }
    g_worker.join();
}

DLLEXPORT intptr_t AGS_EngineOnEvent(int event, intptr_t data) {
    switch (event) {
        case AGSE_PRERENDER:
            drain_queue();
            break;
        case AGSE_ENTERROOM:
            push_event(std::string("enter:") + std::to_string((long long)data));
            break;
        case AGSE_LEAVEROOM:
            push_event(std::string("leave:") + std::to_string((long long)data));
            break;
        case AGSE_POSTRESTOREGAME:
            push_event("restore");
            break;
        default:
            break;
    }
    return 0; // never stop the hook chain
}
