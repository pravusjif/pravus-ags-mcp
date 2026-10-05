using System.ComponentModel;
using System.Linq;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class PropertyReflectionTests
    {
        private enum Mood { Calm, Angry, Happy }

        private sealed class Sample
        {
            [Category("Design")]
            [Description("The name")]
            public string Name { get; set; } = "sample";

            [Category("Stats")]
            public int Count { get; set; }

            public bool Flag { get; set; }

            public float Ratio { get; set; }

            [Category("Behaviour")]
            public Mood Mood { get; set; }

            [ReadOnly(true)]
            public int Id { get; set; } = 7;

            [Browsable(false)]
            public string Hidden { get; set; } = "secret";
        }

        [Fact]
        public void Describe_IncludesBrowsableOnly_WithMetadata()
        {
            var views = PropertyReflection.Describe(new Sample());
            Assert.DoesNotContain(views, v => v.name == "Hidden");

            var name = views.Single(v => v.name == "Name");
            Assert.Equal("Design", name.category);
            Assert.Equal("The name", name.description);
            Assert.Equal("sample", name.value);
            Assert.False(name.readOnly);

            var id = views.Single(v => v.name == "Id");
            Assert.True(id.readOnly);

            var mood = views.Single(v => v.name == "Mood");
            Assert.Equal(new[] { "Calm", "Angry", "Happy" }, mood.options);
            Assert.Equal("Calm", mood.value); // enums render as their name
        }

        [Fact]
        public void Apply_SetsPrimitivesEnumsAndStrings()
        {
            var s = new Sample();
            var changed = PropertyReflection.Apply(s, JObject.Parse(
                @"{""Name"":""hero"",""Count"":5,""Flag"":true,""Ratio"":1.5,""Mood"":""Angry""}"));

            Assert.Equal("hero", s.Name);
            Assert.Equal(5, s.Count);
            Assert.True(s.Flag);
            Assert.Equal(1.5f, s.Ratio);
            Assert.Equal(Mood.Angry, s.Mood);
            Assert.Equal(5, changed.Count);
        }

        [Fact]
        public void Apply_EnumByNumber()
        {
            var s = new Sample();
            PropertyReflection.Apply(s, JObject.Parse(@"{""Mood"":2}"));
            Assert.Equal(Mood.Happy, s.Mood);
        }

        [Fact]
        public void Apply_InvalidEnum_Throws()
        {
            var ex = Assert.Throws<ToolException>(() => PropertyReflection.Apply(new Sample(), JObject.Parse(@"{""Mood"":""Furious""}")));
            Assert.Contains("Furious", ex.Message);
            Assert.Contains("Calm", ex.Message); // lists the options
        }

        [Fact]
        public void Apply_ReadOnlyProperty_Throws()
        {
            var ex = Assert.Throws<ToolException>(() => PropertyReflection.Apply(new Sample(), JObject.Parse(@"{""Id"":9}")));
            Assert.Contains("read-only", ex.Message);
        }

        [Fact]
        public void Apply_UnknownProperty_Throws()
        {
            Assert.Throws<ToolException>(() => PropertyReflection.Apply(new Sample(), JObject.Parse(@"{""Nope"":1}")));
        }

        [Fact]
        public void Apply_MatchesByNameCaseInsensitive()
        {
            var s = new Sample();
            PropertyReflection.Apply(s, JObject.Parse(@"{""count"":42}"));
            Assert.Equal(42, s.Count);
        }
    }
}
