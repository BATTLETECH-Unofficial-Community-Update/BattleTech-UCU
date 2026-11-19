#if B25_SAVE_SHIM_TEST
using System.IO;

namespace BattleTechPort
{
    public static class SaveShimTestGate
    {
        const string Marker = "Mods/.port/bta-combat-saves-test.on";

        public static bool IsEnabled(string playerRoot, string optInValue)
        {
            if (optInValue != "1" || string.IsNullOrWhiteSpace(playerRoot)) return false;
            return File.Exists(Path.Combine(playerRoot, Marker.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
#endif
