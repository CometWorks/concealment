using System;
using System.Globalization;
using Shared.Concealment;
using VRageMath;

namespace ClientPlugin;

internal static class ConcealmentCommands
{
    public static bool IsCommand(string text) => text != null &&
        (text.Equals("!conceal", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("!conceal ", StringComparison.OrdinalIgnoreCase));

    public static string Execute(ConcealmentRuntime runtime, string text, Vector3D? position)
    {
        if (runtime?.Manager == null || !runtime.IsAuthoritative)
            return "Concealment is available only in a loaded world you host. Multiplayer servers control their own concealment.";

        var args = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var command = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
        var manager = runtime.Manager;
        if (args.Length == 3 && command == "reveal" && args[2].Equals("all", StringComparison.OrdinalIgnoreCase))
            return $"{manager.RevealAll()} grids revealed.";

        if (command == "run" || command == "reveal")
        {
            var distance = command == "run" ? runtime.ConfigData.ConcealDistance : 1000;
            if (args.Length > 3 || (args.Length == 3 &&
                (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out distance) ||
                 double.IsNaN(distance) || double.IsInfinity(distance))))
                return $"Usage: !conceal {command} [distance in meters]";
            if (distance < 0)
                return "The distance can't be negative.";
            if (command == "run")
            {
                if (distance == 0)
                    distance = runtime.ConfigData.ConcealDistance;
                return $"{manager.ConcealGrids(distance)} grids concealed.";
            }
            if (!position.HasValue)
                return "You must be controlling an entity.";
            return $"{manager.RevealGridsInSphere(new BoundingSphereD(position.Value, distance))} grids revealed.";
        }

        if (args.Length == 2 && command == "on")
        {
            runtime.ConfigData.Enabled = true;
            return $"Concealment enabled. {manager.ConcealGrids(runtime.ConfigData.ConcealDistance)} grids concealed.";
        }
        if (args.Length == 2 && command == "off")
        {
            runtime.ConfigData.Enabled = false;
            return $"Concealment disabled. {manager.RevealAll()} grids revealed.";
        }
        return "!conceal run [distance], !conceal reveal [distance], !conceal reveal all, !conceal on, !conceal off";
    }
}
