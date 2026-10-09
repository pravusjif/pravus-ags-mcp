using System;
using System.Collections.Generic;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>Describes one kind of game entity: how to list, identify, create and delete it.</summary>
    internal sealed class EntityKind
    {
        public string Name;
        /// <summary>The script-name property, used for uniqueness checks on rename. Null if the type has none.</summary>
        public string NameProperty;
        public Func<Game, IEnumerable<object>> Enumerate;
        /// <summary>Canonical id as a string: the numeric ID, or the name for key-addressed types.</summary>
        public Func<object, string> IdOf;
        /// <summary>Script name / display name; may be the same as the id.</summary>
        public Func<object, string> NameOf;
        /// <summary>Creates and registers a new entity, or null if the type cannot be created here.</summary>
        public Func<Game, JObject, object> Create;
        /// <summary>Removes the entity from the game, or null if it cannot be deleted here.</summary>
        public Action<Game, object> Delete;
        /// <summary>
        /// Optional check of one property value before set_properties/create_entity applies it: (game, entity, property
        /// name, value) returns the value to apply (possibly normalised) or throws a ToolException. Null: no check.
        /// </summary>
        public Func<Game, object, string, JToken, JToken> PrepareProperty;
        /// <summary>Optional follow-up after an entity of this kind is created, changed or deleted (refresh the editor).</summary>
        public Action<Game> AfterChange;
    }

    internal static class EntityRegistry
    {
        private static readonly Dictionary<string, EntityKind> Kinds = Build();

        public static IEnumerable<string> TypeNames => Kinds.Keys.OrderBy(k => k);

        public static EntityKind Get(string type)
        {
            if (type == null || !Kinds.TryGetValue(type.ToLowerInvariant(), out var kind))
                throw new ToolException($"Unknown entity type '{type}'. Known types: {string.Join(", ", TypeNames)}.");
            return kind;
        }

        public static object Resolve(EntityKind kind, Game game, JToken idToken)
        {
            if (idToken == null || idToken.Type == JTokenType.Null)
                throw new ToolException("An 'id' (numeric ID or script name) is required.");
            string want = idToken.Type == JTokenType.Integer ? idToken.Value<long>().ToString() : idToken.ToString();
            foreach (object e in kind.Enumerate(game))
            {
                if (string.Equals(kind.IdOf(e), want, StringComparison.OrdinalIgnoreCase)) return e;
                if (string.Equals(kind.NameOf(e), want, StringComparison.OrdinalIgnoreCase)) return e;
            }
            throw new ToolException($"No {kind.Name} with id or name '{want}'. Use list_entities to see what exists.");
        }

        private static Dictionary<string, EntityKind> Build()
        {
            var kinds = new List<EntityKind>
            {
                new EntityKind
                {
                    Name = "character", NameProperty = "ScriptName",
                    Enumerate = g => g.CharacterFlatList.Cast<object>(),
                    IdOf = e => ((Character)e).ID.ToString(),
                    NameOf = e => ((Character)e).ScriptName,
                    Create = (g, p) =>
                    {
                        var c = new Character { ID = g.RootCharacterFolder.GetAllItemsCount(), ScriptName = EditorInternals.GetFirstAvailableScriptName("cChar") };
                        g.RootCharacterFolder.Items.Add(c);
                        return c;
                    },
                    Delete = (g, e) =>
                    {
                        var c = (Character)e; int id = c.ID;
                        g.RootCharacterFolder.Remove(c);
                        foreach (Character x in g.CharacterFlatList) if (x.ID > id) x.ID--;
                    },
                },
                new EntityKind
                {
                    Name = "inventory", NameProperty = "Name",
                    Enumerate = g => g.InventoryFlatList.Cast<object>(),
                    IdOf = e => ((InventoryItem)e).ID.ToString(),
                    NameOf = e => ((InventoryItem)e).Name,
                    Create = (g, p) =>
                    {
                        // Inventory IDs are 1-based (index 0 is the reserved "no item").
                        var i = new InventoryItem { ID = g.RootInventoryItemFolder.GetAllItemsCount() + 1, Name = EditorInternals.GetFirstAvailableScriptName("iInvItem") };
                        g.RootInventoryItemFolder.Items.Add(i);
                        return i;
                    },
                    Delete = (g, e) =>
                    {
                        var i = (InventoryItem)e; int id = i.ID;
                        g.RootInventoryItemFolder.Remove(i);
                        foreach (InventoryItem x in g.InventoryFlatList) if (x.ID > id) x.ID--;
                    },
                },
                new EntityKind
                {
                    Name = "dialog", NameProperty = "Name",
                    Enumerate = g => g.DialogFlatList.Cast<object>(),
                    IdOf = e => ((Dialog)e).ID.ToString(),
                    NameOf = e => ((Dialog)e).Name,
                    Create = (g, p) =>
                    {
                        var d = new Dialog { ID = g.RootDialogFolder.GetAllItemsCount(), Name = EditorInternals.GetFirstAvailableScriptName("dDialog") };
                        g.RootDialogFolder.Items.Add(d);
                        return d;
                    },
                    Delete = (g, e) =>
                    {
                        var d = (Dialog)e; int id = d.ID;
                        g.RootDialogFolder.Remove(d);
                        foreach (Dialog x in g.DialogFlatList) if (x.ID > id) x.ID--;
                    },
                },
                new EntityKind
                {
                    Name = "gui", NameProperty = "Name",
                    Enumerate = g => g.GUIFlatList.Cast<object>(),
                    IdOf = e => ((GUI)e).ID.ToString(),
                    NameOf = e => ((GUI)e).Name,
                    Create = (g, p) =>
                    {
                        var res = g.Settings.CustomResolution;
                        var gui = new NormalGUI(Math.Min(res.Width, 320), Math.Min(res.Height, 200))
                        {
                            ID = g.RootGUIFolder.GetAllItemsCount(),
                            Name = EditorInternals.GetFirstAvailableScriptName("gGui"),
                        };
                        g.RootGUIFolder.Items.Add(gui);
                        return gui;
                    },
                    Delete = (g, e) =>
                    {
                        var gui = (GUI)e; int id = gui.ID;
                        g.RootGUIFolder.Remove(gui);
                        foreach (GUI x in g.GUIFlatList) if (x.ID > id) x.ID--;
                    },
                },
                new EntityKind
                {
                    Name = "view", NameProperty = "Name",
                    Enumerate = g => g.ViewFlatList.Cast<object>(),
                    IdOf = e => ((View)e).ID.ToString(),
                    NameOf = e => ((View)e).Name,
                    Create = (g, p) =>
                    {
                        var v = new View { ID = g.FindAndAllocateAvailableViewID() };
                        v.Name = "View" + v.ID;
                        g.RootViewFolder.Items.Add(v);
                        return v;
                    },
                    // Views recycle their ID via ViewDeleted instead of renumbering the others.
                    Delete = (g, e) => { var v = (View)e; g.RootViewFolder.Remove(v); g.ViewDeleted(v.ID); },
                },
                new EntityKind
                {
                    Name = "cursor", NameProperty = "Name",
                    Enumerate = g => g.Cursors.Cast<object>(),
                    IdOf = e => ((MouseCursor)e).ID.ToString(),
                    NameOf = e => ((MouseCursor)e).Name,
                    Create = (g, p) =>
                    {
                        var c = new MouseCursor { ID = g.Cursors.Count };
                        c.Name = "Cursor" + c.ID;
                        g.Cursors.Add(c);
                        return c;
                    },
                    Delete = (g, e) =>
                    {
                        var c = (MouseCursor)e; int id = c.ID;
                        g.Cursors.Remove(c);
                        foreach (MouseCursor x in g.Cursors) if (x.ID > id) x.ID--;
                    },
                },
                new EntityKind
                {
                    // Fonts and audio clips are backed by resource files: audio clips are created by import_audio
                    // (AudioTools); fonts cannot be created yet.
                    Name = "font", NameProperty = null,
                    Enumerate = g => g.Fonts.Cast<object>(),
                    IdOf = e => ((Font)e).ID.ToString(),
                    NameOf = e => ((Font)e).ScriptID,
                },
                new EntityKind
                {
                    Name = "audioclip", NameProperty = "ScriptName",
                    Enumerate = g => g.AudioClipFlatList.Cast<object>(),
                    IdOf = e => ((AudioClip)e).ID.ToString(),
                    NameOf = e => ((AudioClip)e).ScriptName,
                    PrepareProperty = (g, e, prop, value) => prop == "Type" ? AudioTypeIdToken(g, value) : value,
                    AfterChange = g => EditorInternals.RefreshAudioTree(),
                },
                new EntityKind
                {
                    // Audio types (Sound, Music, Ambient Sound...). TypeIDs must stay 1..N: the build writes types by
                    // position. Mirrors AudioComponent.CreateNewAudioClipType / DeleteAudioClipType.
                    Name = "audiocliptype", NameProperty = null,
                    Enumerate = g => g.AudioClipTypes.Cast<object>(),
                    IdOf = e => ((AudioClipType)e).TypeID.ToString(),
                    NameOf = e => ((AudioClipType)e).Name,
                    Create = (g, p) =>
                    {
                        string name = UniqueAudioTypeName(g, null, RequireName(p));
                        var type = new AudioClipType(g.AudioClipTypes.Count + 1, name, 0, 0, false, CrossfadeSpeed.No);
                        g.AudioClipTypes.Add(type);
                        return type;
                    },
                    Delete = (g, e) =>
                    {
                        var type = (AudioClipType)e; int id = type.TypeID;
                        if (g.AudioClipTypes.Count <= 1)
                            throw new ToolException("A game needs at least one audio type.");
                        int clips = g.AudioClipFlatList.Count(c => c.Type == id);
                        if (clips > 0)
                            throw new ToolException($"Audio type '{type.Name}' is used by {clips} audio clip(s); change their Type first.");
                        List<AudioClipFolder> folders = AudioTools.AllFolders(g.RootAudioClipFolder);
                        string usedBy = string.Join(", ", folders.Where(f => f.DefaultType == id).Select(f => f.Name));
                        if (usedBy.Length > 0)
                            throw new ToolException($"Audio type '{type.Name}' is the default type of folder(s) {usedBy}; change their DefaultType first.");
                        g.AudioClipTypes.Remove(type);
                        foreach (AudioClipType t in g.AudioClipTypes) if (t.TypeID > id) t.TypeID--;
                        foreach (AudioClip c in g.AudioClipFlatList) if (c.Type > id) c.Type--;
                        foreach (AudioClipFolder f in folders) if (f.DefaultType > id) f.DefaultType--;
                    },
                    PrepareProperty = (g, e, prop, value) =>
                        prop == "Name" ? new JValue(UniqueAudioTypeName(g, (AudioClipType)e, value?.ToString())) : value,
                    AfterChange = g =>
                    {
                        AudioClipTypeTypeConverter.RefreshAudioClipTypeList();
                        EditorInternals.RefreshAudioTree();
                    },
                },
                new EntityKind
                {
                    // Audio folders, addressed by name (the root is the game's top Audio node, usually "Main"). Their
                    // defaults matter at run time: a clip whose volume/priority/repeat is Inherit takes the nearest
                    // folder's, and new clips take DefaultType/DefaultBundlingType. Names are kept unique so they
                    // stay addressable; create takes an optional 'Parent' folder name.
                    Name = "audiofolder", NameProperty = null,
                    Enumerate = g => AudioTools.AllFolders(g.RootAudioClipFolder).Cast<object>(),
                    IdOf = e => ((AudioClipFolder)e).Name,
                    NameOf = e => ((AudioClipFolder)e).Name,
                    Create = (g, p) =>
                    {
                        string name = UniqueAudioFolderName(g, null, RequireName(p));
                        AudioClipFolder parent = AudioTools.ResolveFolder(g, StringProp(p, "Parent", null));
                        AudioClipFolder folder = parent.CreateChildFolder(name); // inherits DefaultType and DefaultBundlingType
                        parent.SubFolders.Add(folder);
                        return folder;
                    },
                    Delete = (g, e) =>
                    {
                        var folder = (AudioClipFolder)e;
                        if (folder == g.RootAudioClipFolder)
                            throw new ToolException("The root audio folder cannot be deleted.");
                        if (folder.Items.Count > 0 || folder.SubFolders.Count > 0)
                            throw new ToolException($"Audio folder '{folder.Name}' is not empty ({folder.Items.Count} clip(s), {folder.SubFolders.Count} subfolder(s)). Delete or move them first.");
                        AudioTools.FindParentFolder(g.RootAudioClipFolder, folder)?.SubFolders.Remove(folder);
                    },
                    PrepareProperty = (g, e, prop, value) =>
                    {
                        switch (prop)
                        {
                            case "Name": return new JValue(UniqueAudioFolderName(g, (AudioClipFolder)e, value?.ToString()));
                            case "DefaultType": return AudioTypeIdToken(g, value);
                            case "DefaultVolume":
                                if (value == null || value.Type != JTokenType.Integer || (int)value < 0 || (int)value > 100)
                                    throw new ToolException("DefaultVolume must be an integer 0..100.");
                                return value;
                            default: return value;
                        }
                    },
                    AfterChange = g => EditorInternals.RefreshAudioTree(),
                },
                new EntityKind
                {
                    Name = "globalvariable", NameProperty = "Name",
                    Enumerate = g => g.GlobalVariables.ToList().Cast<object>(),
                    IdOf = e => ((GlobalVariable)e).Name,
                    NameOf = e => ((GlobalVariable)e).Name,
                    Create = (g, p) =>
                    {
                        string name = RequireName(p);
                        if (g.IsScriptNameAlreadyUsed(name, null) || g.GlobalVariables[name] != null)
                            throw new ToolException($"The name '{name}' is already in use.");
                        var gv = new GlobalVariable
                        {
                            Name = name,
                            Type = StringProp(p, "Type", "int"),
                            DefaultValue = StringProp(p, "DefaultValue", string.Empty),
                        };
                        g.GlobalVariables.Add(gv);
                        return gv;
                    },
                    Delete = (g, e) => g.GlobalVariables.Remove((GlobalVariable)e),
                },
                new EntityKind
                {
                    Name = "customproperty", NameProperty = "Name",
                    Enumerate = g => g.PropertySchema.PropertyDefinitions.Cast<object>(),
                    IdOf = e => ((CustomPropertySchemaItem)e).Name,
                    NameOf = e => ((CustomPropertySchemaItem)e).Name,
                    Create = (g, p) =>
                    {
                        string name = RequireName(p);
                        if (g.PropertySchema.PropertyDefinitions.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            throw new ToolException($"A custom property named '{name}' already exists.");
                        var item = new CustomPropertySchemaItem
                        {
                            Name = name,
                            Description = StringProp(p, "Description", string.Empty),
                            DefaultValue = StringProp(p, "DefaultValue", string.Empty),
                        };
                        g.PropertySchema.PropertyDefinitions.Add(item);
                        return item;
                    },
                    Delete = (g, e) => g.PropertySchema.PropertyDefinitions.Remove((CustomPropertySchemaItem)e),
                },
                new EntityKind
                {
                    // The game-wide settings object: a singleton, addressed as "settings".
                    Name = "setting", NameProperty = null,
                    Enumerate = g => new object[] { g.Settings },
                    IdOf = e => "settings",
                    NameOf = e => "settings",
                },
            };
            return kinds.ToDictionary(k => k.Name, k => k);
        }

        /// <summary>An audio type given by TypeID or name, as its TypeID; throws if there is no such type.</summary>
        private static JToken AudioTypeIdToken(Game game, JToken value) =>
            new JValue(AudioTools.ResolveClipType(game.AudioClipTypes, value?.ToString()).TypeID);

        private static string UniqueAudioTypeName(Game game, AudioClipType self, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new ToolException("An audio type needs a non-empty Name.");
            if (game.AudioClipTypes.Any(t => t != self && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"An audio type named '{name}' already exists.");
            return name;
        }

        private static string UniqueAudioFolderName(Game game, AudioClipFolder self, string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new ToolException("An audio folder needs a non-empty Name.");
            if (AudioTools.AllFolders(game.RootAudioClipFolder).Any(f => f != self && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"An audio folder named '{name}' already exists.");
            return name;
        }

        private static string RequireName(JObject props)
        {
            string name = StringProp(props, "Name", null);
            if (string.IsNullOrWhiteSpace(name))
                throw new ToolException("A 'Name' is required in properties to create this entity.");
            return name.Trim();
        }

        private static string StringProp(JObject props, string key, string fallback)
        {
            if (props == null) return fallback;
            JToken token = props[key] ?? props.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Value;
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }
    }
}
