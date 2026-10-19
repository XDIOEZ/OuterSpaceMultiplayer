using System;
using System.Runtime.Serialization;
using Newtonsoft.Json.Serialization;
using Script.FreeWorldNet.Archive.NetBean.Records;

namespace FreeWorldMultiplayer
{
    internal static class NativeArchiveCreation
    {
        internal static void Configure(Type type, JsonObjectContract contract)
        {
            if (type != typeof(MessageRecorder)) return;

            // Its gameplay constructor calls UserBean.getTime(), but a detached archive
            // is restored before UserBean owns the loaded game data. This record has
            // only persisted fields; let the existing field contract restore all three,
            // including the saved recordTime, without running gameplay initialization.
            contract.OverrideCreator = arguments => FormatterServices.GetUninitializedObject(typeof(MessageRecorder));
            contract.CreatorParameters.Clear();
        }
    }
}
