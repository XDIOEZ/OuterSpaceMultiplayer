using System;
using FreeWorldMultiplayer;

internal static class UnauthenticatedFrameChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const int kb = 1024;
        check(UnauthenticatedFramePolicy.AllowsFrame(true, false, kb) &&
            UnauthenticatedFramePolicy.AllowsFrame(true, false, 16 * kb),
            "small pre-auth hello and auth payloads fit the handshake budget");
        check(!UnauthenticatedFramePolicy.AllowsFrame(true, false, 16 * kb + 1),
            "the pre-auth size limit is strict at its byte boundary");
        int decoded = 0;
        foreach (int length in new[] { 1, 8, 32, 128 })
        {
            int bytes = length * 1024 * 1024;
            if (UnauthenticatedFramePolicy.AllowsFrame(true, false, bytes)) decoded++;
        }
        check(decoded == 0, "1 / 8 / 32 / 128 MiB pre-auth frames never reach JSON decoding");
        check(UnauthenticatedFramePolicy.AllowsFrame(true, true, 32 * 1024 * 1024) &&
            UnauthenticatedFramePolicy.AllowsFrame(true, true, 128 * 1024 * 1024),
            "authenticated peers may still submit full-size native map frames");
        check(UnauthenticatedFramePolicy.AllowsFrame(false, false, 32 * 1024 * 1024),
            "game clients may receive the host's large welcome and map views before marking its peer");
        check(!UnauthenticatedFramePolicy.AllowsFrame(true, true, 128 * 1024 * 1024 + 1) &&
            !UnauthenticatedFramePolicy.AllowsFrame(false, false, 0),
            "invalid lengths are rejected regardless of session role");
        check(UnauthenticatedFramePolicy.IsHandshakeKind("hello") &&
            UnauthenticatedFramePolicy.IsHandshakeKind("auth"),
            "only the two actual credential-stage message kinds pass the pre-auth whitelist");
        check(!UnauthenticatedFramePolicy.IsHandshakeKind("map") &&
            !UnauthenticatedFramePolicy.IsHandshakeKind("personalUpdate") &&
            !UnauthenticatedFramePolicy.IsHandshakeKind("uids") &&
            !UnauthenticatedFramePolicy.IsHandshakeKind("ping") &&
            !UnauthenticatedFramePolicy.IsHandshakeKind(null),
            "an anonymous peer cannot dispatch maps, personal data or unrecognized messages");
    }
}
