namespace FreeWorldMultiplayer
{
    // Host-side native battle input validation runs for every received pilot
    // command. Check the same six fields in the original order without
    // creating a float[] or invoking LINQ Any for each command.
    internal static class BattlePilotFiniteFields
    {
        internal static bool Valid(float x, float y, float z,
            float throttle, float turn, float strafe)
        {
            return !float.IsNaN(x) && !float.IsInfinity(x)
                && !float.IsNaN(y) && !float.IsInfinity(y)
                && !float.IsNaN(z) && !float.IsInfinity(z)
                && !float.IsNaN(throttle) && !float.IsInfinity(throttle)
                && !float.IsNaN(turn) && !float.IsInfinity(turn)
                && !float.IsNaN(strafe) && !float.IsInfinity(strafe);
        }
    }
}
