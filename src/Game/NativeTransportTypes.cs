using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FreeWorldMultiplayer
{
    public sealed partial class GameBridge
    {
        private sealed class CargoLine { public long Uid; public int Count; }
        private sealed class Manifest
        {
            public string VehicleId;
            public double DurationSeconds;
            public List<CargoLine> Items = new List<CargoLine>();
            public List<long> Heroes = new List<long>();
            public List<long> Animals = new List<long>();
            public bool UseSpaceElevator;
            public long MissionUid = -1;
        }
        private sealed class Cargo
        {
            public List<string> Items = new List<string>();
            public List<long> Heroes = new List<long>();
            public List<long> Animals = new List<long>();
            public long ExpeditionUid = -1;
            public bool Delivered;
        }
    }
}
