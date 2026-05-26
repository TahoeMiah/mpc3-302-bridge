using System.Collections.Generic;

namespace Mpc3TcpBridge.State
{
    // Canonical button identifiers used in every TCP message and console
    // command. Keep them short and lowercase so a human can type them at
    // an `nc` session without quoting.
    //
    // The MPC3-302 has 12 lit, addressable buttons: Power + Mute + 10
    // programmable buttons (the rotary is read separately as a volume delta).
    public static class ButtonNames
    {
        public const string Power = "power";
        public const string Mute  = "mute";

        // Returns "btn01".."btn10" for the ten programmable buttons.
        public static string Programmable(int index1Based)
        {
            // Two-digit zero-pad keeps client-side displays sortable.
            return index1Based < 10 ? "btn0" + index1Based : "btn" + index1Based;
        }

        // All 12 names in stable order - used in the `hello` event so a
        // client can enumerate the panel without guessing the layout.
        public static IEnumerable<string> All()
        {
            yield return Power;
            yield return Mute;
            for (int i = 1; i <= 10; i++) yield return Programmable(i);
        }
    }
}
