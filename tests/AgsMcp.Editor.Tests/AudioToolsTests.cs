using System.Collections.Generic;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class AudioToolsTests
    {
        [Theory]
        [InlineData("door.wav", AudioClipFileType.WAV)]
        [InlineData(@"C:\x\Theme.OGG", AudioClipFileType.OGG)]
        [InlineData("a.mp3", AudioClipFileType.MP3)]
        [InlineData("a.voc", AudioClipFileType.VOC)]
        [InlineData("a.mid", AudioClipFileType.MIDI)]
        [InlineData("a.mod", AudioClipFileType.MOD)]
        [InlineData("a.xm", AudioClipFileType.MOD)]
        [InlineData("a.s3m", AudioClipFileType.MOD)]
        [InlineData("a.it", AudioClipFileType.MOD)]
        public void MapsExtensionsToFileTypes(string file, AudioClipFileType expected)
        {
            Assert.Equal(expected, AudioTools.AudioFileType(file));
        }

        [Theory]
        [InlineData("a.flac")]
        [InlineData("noextension")]
        [InlineData("")]
        public void RejectsUnsupportedFiles(string file)
        {
            var e = Assert.Throws<ToolException>(() => AudioTools.AudioFileType(file));
            Assert.Contains(".ogg", e.Message);
        }

        [Theory]
        [InlineData(1, @"Audio\beep.wav", "au000001.wav")]
        [InlineData(26, "x.ogg", "au00001A.ogg")]
        [InlineData(255, @"C:\snd\Theme.MP3", "au0000FF.MP3")]
        public void DerivesCacheFileNameLikeTheEditor(int index, string source, string expected)
        {
            Assert.Equal(expected, AudioTools.AudioCacheFileName(index, source));
        }

        [Theory]
        [InlineData("door_knock", "aDoor_knock")]
        [InlineData("Theme", "aTheme")]
        [InlineData("my sound-1", "aMy_sound_1")]
        [InlineData("1up", "a1up")]
        [InlineData("", "aClip")]
        [InlineData(null, "aClip")]
        public void MakesScriptNamesLikeTheEditor(string stem, string expected)
        {
            Assert.Equal(expected, AudioTools.MakeAudioScriptName(stem, _ => false));
        }

        [Fact]
        public void NumbersScriptNamesUntilUnused()
        {
            var used = new HashSet<string> { "aBeep", "aBeep1" };
            Assert.Equal("aBeep2", AudioTools.MakeAudioScriptName("beep", used.Contains));
        }

        [Theory]
        [InlineData("door.wav")]
        [InlineData("  Theme.ogg ")]
        public void AcceptsPlainUploadFileNames(string name)
        {
            Assert.Equal(name.Trim(), AudioTools.CheckUploadFileName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"sub\door.wav")]
        [InlineData("../door.wav")]
        [InlineData("door.txt")]
        public void RejectsBadUploadFileNames(string name)
        {
            Assert.Throws<ToolException>(() => AudioTools.CheckUploadFileName(name));
        }

        [Theory]
        [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\Audio\door.wav", @"Audio\door.wav")]
        [InlineData(@"C:\Games\Foo\", @"c:\games\foo\door.wav", "door.wav")]
        [InlineData(@"C:\Games\Foo", @"C:\Games\FooBar\door.wav", @"C:\Games\FooBar\door.wav")]
        [InlineData(@"C:\Games\Foo", @"D:\snd\door.wav", @"D:\snd\door.wav")]
        public void StoresSourcePathsRelativeToTheGameWhenInside(string game, string full, string expected)
        {
            Assert.Equal(expected, AudioTools.ProjectRelativePath(game, full));
        }

        [Fact]
        public void ParsesAudioEnumsCaseInsensitively()
        {
            Assert.Equal(AudioClipPriority.VeryHigh, AudioTools.ParseAudioEnum<AudioClipPriority>("veryhigh", "priority"));
            Assert.Equal(InheritableBool.True, AudioTools.ParseAudioEnum<InheritableBool>("TRUE", "repeat"));
            Assert.Equal(AudioFileBundlingType.InSeparateVOX, AudioTools.ParseAudioEnum<AudioFileBundlingType>("InSeparateVOX", "bundling"));
        }

        [Fact]
        public void RejectsUnknownAudioEnumWithTheValidNames()
        {
            var e = Assert.Throws<ToolException>(() => AudioTools.ParseAudioEnum<AudioClipPriority>("Loud", "priority"));
            Assert.Contains("priority", e.Message);
            Assert.Contains("VeryHigh", e.Message);
        }

        private static List<AudioClipType> DefaultTypes() => new List<AudioClipType>
        {
            new AudioClipType(1, "Ambient Sound", 1, 0, true, CrossfadeSpeed.No),
            new AudioClipType(2, "Music", 1, 30, true, CrossfadeSpeed.No),
            new AudioClipType(3, "Sound", 0, 0, false, CrossfadeSpeed.No),
        };

        [Theory]
        [InlineData("3", 3)]
        [InlineData("music", 2)]
        [InlineData("Ambient Sound", 1)]
        [InlineData("eAudioTypeAmbientSound", 1)]
        public void ResolvesAudioTypesByIdNameOrScriptId(string value, int expectedId)
        {
            Assert.Equal(expectedId, AudioTools.ResolveClipType(DefaultTypes(), value).TypeID);
        }

        [Fact]
        public void UnknownAudioTypeListsTheTypes()
        {
            var e = Assert.Throws<ToolException>(() => AudioTools.ResolveClipType(DefaultTypes(), "Voice"));
            Assert.Contains("3 Sound", e.Message);
        }

        [Fact]
        public void FindsWholeWordScriptUsesOnly()
        {
            string text = "function x() {\r\n  aDoor.Play();\r\n  aDoorOpen.Play();\r\n  // aDoor again\r\n  my_aDoor = 1;\r\n}";
            Assert.Equal(new[] { 2, 4 }, AudioTools.FindScriptNameUses(text, "aDoor").ToArray());
            Assert.Empty(AudioTools.FindScriptNameUses(null, "aDoor"));
        }

        [Fact]
        public void ListsFoldersDepthFirstAndFindsParents()
        {
            var root = new AudioClipFolder("Main");
            var music = new AudioClipFolder("Music");
            var themes = new AudioClipFolder("Themes");
            var sounds = new AudioClipFolder("Sounds");
            root.SubFolders.Add(music);
            root.SubFolders.Add(sounds);
            music.SubFolders.Add(themes);

            Assert.Equal(new[] { "Main", "Music", "Themes", "Sounds" }, AudioTools.AllFolders(root).Select(f => f.Name).ToArray());
            Assert.Same(music, AudioTools.FindParentFolder(root, themes));
            Assert.Same(root, AudioTools.FindParentFolder(root, sounds));
            Assert.Null(AudioTools.FindParentFolder(root, root));
        }

        [Fact]
        public void ImportSchemaIsFlat()
        {
            JObject schema = AudioTools.ImportAudioSchema();
            Assert.Equal("object", (string)schema["type"]);
            Assert.Equal("string", (string)schema["properties"]["path"]["type"]);
            Assert.Equal("integer", (string)schema["properties"]["volume"]["type"]);
            Assert.Contains("InSeparateVOX", schema["properties"]["bundling"]["enum"].Values<string>());
            Assert.Null(schema["required"]); // path/base64 is "exactly one of", checked at call time
            string json = schema.ToString();
            Assert.DoesNotContain("anyOf", json);
            Assert.DoesNotContain("oneOf", json);
        }
    }
}
