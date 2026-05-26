namespace Mpc3TcpBridge.State
{
    // Compact Framework 3.5 on Crestron 3-Series omits int.TryParse, so this
    // helper stands in. Wraps Int32.Parse in try/catch and defaults to zero
    // on failure - close enough to the TryParse contract for our callers.
    internal static class Parse
    {
        public static bool TryInt(string s, out int value)
        {
            if (s == null) { value = 0; return false; }
            try { value = int.Parse(s.Trim()); return true; }
            catch { value = 0; return false; }
        }
    }
}
