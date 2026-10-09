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

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "agsplugin.h"
#include "json_min.h"

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

// ---- event log (main thread only) ------------------------------------------
static std::deque<std::string> g_events;

// ---- server ----------------------------------------------------------------
static std::atomic<bool> g_server_running(false);
static SOCKET g_listen = INVALID_SOCKET;
static std::thread g_worker;
static int g_port = 0;

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

// Post a key down/up pair to the game window so SDL sees it as a real key press.
static bool post_window_key(int vk) {
    HWND hwnd = engine->GetWindowHandle();
    if (!hwnd) return false;
    UINT scan = MapVirtualKeyA(vk, MAPVK_VK_TO_VSC);
    LPARAM down = 1 | ((LPARAM)scan << 16);
    PostMessageA(hwnd, WM_KEYDOWN, vk, down);
    PostMessageA(hwnd, WM_KEYUP, vk, down | ((LPARAM)1 << 30) | ((LPARAM)1 << 31));
    return true;
}

static jm::Value cmd_screenshot() {
    jm::Value r = jm::Value::MkObj();
    if (!fn_savescreenshot) {
        r.set("ok", false);
        r.set("error", "SaveScreenShot is not available");
        return r;
    }
    const char* scriptPath = "$SAVEGAMEDIR$/agsmcp.bmp";
    int res = fn_savescreenshot(scriptPath);
    char buf[1024];
    buf[0] = 0;
    engine->ResolveFilePath(scriptPath, buf, sizeof(buf));
    std::string path(buf);
    r.set("ok", !path.empty());
    r.set("result", res);
    r.set("path", path);
    return r;
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
        // Game.SimulateKeyPress treats codes 1..26 as Ctrl+A..Z, so Backspace (8), Tab (9) and
        // Enter (13) would arrive as Ctrl+H/I/M. Post those as real window key messages instead.
        int vk = code == 8 ? VK_BACK : code == 9 ? VK_TAB : code == 13 ? VK_RETURN : 0;
        if (vk && post_window_key(vk)) {
            r.set("ok", true);
            r.set("via", "window");
            return r;
        }
        if (!fn_simkey) { r.set("ok", false); r.set("error", "SimulateKeyPress is not available"); return r; }
        fn_simkey(code);
        r.set("ok", true);
        return r;
    }
    if (cmd == "hover_name") {
        char buf[256];
        buf[0] = 0;
        if (fn_getlocationname) fn_getlocationname(argi(args, "x"), argi(args, "y"), buf);
        r.set("ok", true);
        r.set("name", safe_str(buf, sizeof(buf)));
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
        engine->QueueGameScriptFunction(name.c_str(), 1, n, a1, a2);
        r.set("ok", true);
        r.set("queued", true);
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

// Drain the command queue on the game main thread.
static void drain_queue() {
    resolve_functions();
    for (;;) {
        std::shared_ptr<Request> req;
        {
            std::lock_guard<std::mutex> lk(g_qmtx);
            if (g_queue.empty()) break;
            req = g_queue.front();
            g_queue.pop_front();
        }
        jm::Value result = handle_command(req->cmd, req->args);
        {
            std::lock_guard<std::mutex> lk(req->m);
            req->result = result;
            req->done = true;
        }
        req->cv.notify_all();
    }
}

// Enqueue a command and wait for the main thread to process it.
static jm::Value call_on_main(const std::string& cmd, const jm::Value& args) {
    std::shared_ptr<Request> req = std::make_shared<Request>();
    req->cmd = cmd;
    req->args = args;
    {
        std::lock_guard<std::mutex> lk(g_qmtx);
        g_queue.push_back(req);
    }
    std::unique_lock<std::mutex> lk(req->m);
    if (!req->cv.wait_for(lk, std::chrono::seconds(10), [&] { return req->done; })) {
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
    std::string buf;
    char tmp[4096];
    while (g_server_running) {
        int n = recv(c, tmp, sizeof(tmp), 0);
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

static void server_main() {
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return;

    g_listen = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_listen == INVALID_SOCKET) { WSACleanup(); return; }

    BOOL reuse = TRUE;
    setsockopt(g_listen, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse));

    sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons((unsigned short)g_port);
    inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

    if (bind(g_listen, (sockaddr*)&addr, sizeof(addr)) != 0 || listen(g_listen, 4) != 0) {
        closesocket(g_listen);
        g_listen = INVALID_SOCKET;
        WSACleanup();
        log_info("failed to bind the loopback server socket");
        return;
    }
    log_info("MCP engine bridge listening");

    while (g_server_running) {
        SOCKET c = accept(g_listen, 0, 0);
        if (c == INVALID_SOCKET) break;
        handle_client(c);
        closesocket(c);
    }

    if (g_listen != INVALID_SOCKET) { closesocket(g_listen); g_listen = INVALID_SOCKET; }
    WSACleanup();
}

static void push_event(const std::string& e) {
    g_events.push_back(e);
    while (g_events.size() > 30) g_events.pop_front();
}

// ------------------------------------------------------------- plugin exports

DLLEXPORT const char* AGS_GetPluginName() {
    return "AGS MCP Engine Bridge";
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

DLLEXPORT void AGS_EngineShutdown() {
    g_server_running = false;
    if (g_listen != INVALID_SOCKET) {
        closesocket(g_listen); // unblock accept()
        g_listen = INVALID_SOCKET;
    }
    if (g_worker.joinable()) g_worker.join();
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
