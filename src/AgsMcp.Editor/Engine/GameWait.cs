using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Engine
{
    /// <summary>
    /// Pure evaluation of game_wait_until conditions against a state object returned by the engine
    /// plugin, so run-loop tests can exercise it without a running game.
    /// </summary>
    public static class GameWait
    {
        public const string ConditionRoom = "room";
        public const string ConditionInterfaceEnabled = "interfaceEnabled";
        public const string ConditionIdle = "idle";
        public const string ConditionReady = "ready";

        /// <summary>
        /// Whether the given state satisfies the condition. For "room", targetRoom is the room number to
        /// reach (required); "interfaceEnabled" waits for the UI to be usable; "idle" waits for the player
        /// to stop walking and animating; "ready" needs both (the player can act again).
        /// </summary>
        public static bool IsSatisfied(JObject state, string condition, int? targetRoom)
        {
            if (state == null) return false;

            switch (condition)
            {
                case ConditionRoom:
                    if (!targetRoom.HasValue) return false;
                    return (int?)state["room"] == targetRoom.Value;

                case ConditionInterfaceEnabled:
                    return (int?)state["interfaceEnabled"] == 1;

                case ConditionReady:
                    return IsSatisfied(state, ConditionInterfaceEnabled, null) && IsSatisfied(state, ConditionIdle, null);

                case ConditionIdle:
                    JObject player = state["player"] as JObject;
                    if (player == null) return false;
                    bool walking = (bool?)player["walking"] ?? false;
                    bool animating = (bool?)player["animating"] ?? false;
                    return !walking && !animating;

                default:
                    return false;
            }
        }
    }
}
