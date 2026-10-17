using System;
using System.Collections.Generic;
using System.Linq;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAILED: " + name);
        checks++;
    }

    private static FWExpedition Expedition(long uid, params long[] heroIds)
    {
        var result = new FWExpedition { Id = uid, Payload = "exp-" + uid };
        result.roleUidList.AddRange(heroIds);
        return result;
    }

    private static FWHeroBean Hero(long uid, long mapId = -1, long fleetId = -1)
    { return new FWHeroBean { Id = uid, Payload = "hero-" + uid, starMapId = mapId, fleetUid = fleetId }; }

    private static FWAnimalBean Animal(long uid, long mapId = -1, long fleetId = -1)
    { return new FWAnimalBean { Id = uid, Payload = "animal-" + uid, starMapId = mapId, fleetUid = fleetId }; }

    // The old implementation is intentionally independent of the production
    // method. It eagerly allocates both UID sets and always traverses every
    // resident hero/animal when at least one expedition exists.
    private static GameBridge.TransitState Legacy(FWGameData data, string playerId)
    {
        var result = new GameBridge.TransitState { PlayerId = playerId };
        if (data.expeditionSeq.Count == 0) return result;
        var heroes = new HashSet<long>();
        var animals = new HashSet<long>();
        foreach (FWExpedition expedition in data.expeditionSeq)
        {
            result.Expeditions.Add(expedition.Payload);
            foreach (long uid in expedition.roleUidList) heroes.Add(uid);
            foreach (long uid in expedition.animalUidList) animals.Add(uid);
        }
        foreach (FWHeroBean hero in data.heros)
            if (heroes.Contains(hero.getUID()) && hero.starMapId < 0 && hero.fleetUid < 0)
                result.Heroes.Add(hero.Payload);
        foreach (FWAnimalBean animal in data.animals)
            if (animals.Contains(animal.getUID()) && animal.starMapId < 0 && animal.fleetUid < 0)
                result.Animals.Add(animal.Payload);
        return result;
    }

    private static void Same(GameBridge.TransitState expected, GameBridge.TransitState actual, string label)
    {
        Check(actual.PlayerId == expected.PlayerId, label + ": player isolation");
        Check(actual.Expeditions.SequenceEqual(expected.Expeditions), label + ": expedition order");
        Check(actual.Heroes.SequenceEqual(expected.Heroes), label + ": hero order");
        Check(actual.Animals.SequenceEqual(expected.Animals), label + ": animal order");
    }

    private static void Reset(FWGameData data)
    {
        data.heros.Enumerations = data.heros.Visited = 0;
        data.animals.Enumerations = data.animals.Visited = 0;
    }

    private static int Main()
    {
        try
        {
            var bridge = new GameBridge();
            var empty = new FWGameData();
            bridge.Players.Add("empty", empty);
            GameBridge.TransitState emptyState = bridge.TestCapture("empty");
            Same(Legacy(empty, "empty"), emptyState, "no expedition");
            Check(empty.heros.Enumerations == 0 && empty.animals.Enumerations == 0,
                "no expedition never scans resident entities");
            Check(bridge.CapturedCount == 0, "empty player emits no cache or sampled transit identities");

            var cargo = new FWGameData();
            bridge.Players.Add("cargo", cargo);
            for (int i = 0; i < 6; i++) cargo.expeditionSeq.Add(Expedition(100 + i));
            const int residentCount = 4096, frames = 300;
            for (int i = 0; i < residentCount; i++)
            {
                cargo.heros.Add(Hero(10000 + i));
                cargo.animals.Add(Animal(20000 + i));
            }
            // Cold cache and warm cache both have the same no-passenger behavior.
            var firstCargo = bridge.TestCapture("cargo");
            Check(firstCargo.Expeditions.Count == 6 && firstCargo.Heroes.Count == 0 && firstCargo.Animals.Count == 0,
                "cargo-only expeditions keep complete expedition manifests");
            Check(cargo.heros.Enumerations == 0 && cargo.animals.Enumerations == 0,
                "cargo-only cold frame avoids both full resident list scans");
            var previousEntry = bridge.Cached(100);
            long previousCacheHits = NativeWire.CacheHits;
            bridge.BeginFrame();
            for (int i = 0; i < frames; i++)
            {
                GameBridge.TransitState state = bridge.TestCapture("cargo");
                if (i == 0) Same(firstCargo, state, "warm cargo frame");
                if (state.Expeditions.Count != 6 || state.Heroes.Count != 0 || state.Animals.Count != 0)
                    throw new InvalidOperationException("FAILED: cargo manifest changed");
            }
            Check(ReferenceEquals(previousEntry, bridge.Cached(100)) && NativeWire.CacheHits > previousCacheHits,
                "cargo-only frames preserve existing cached encoded entity reuse");
            Check(cargo.heros.Visited == 0 && cargo.animals.Visited == 0
                && cargo.heros.Enumerations == 0 && cargo.animals.Enumerations == 0,
                "300 production cargo-only frames avoid both resident list enumerations");
            Check(bridge.CapturedId(100) && bridge.CapturedId(105) && !bridge.CapturedId(10000),
                "cargo-only sampling tracks expedition identities but not unrelated residents");

            Reset(cargo);
            GameBridge.TransitState expectedCargo = null;
            for (int i = 0; i < frames; i++) expectedCargo = Legacy(cargo, "cargo");
            Same(expectedCargo, firstCargo, "cargo legacy byte-equivalent contents");
            Check(cargo.heros.Visited == (long)residentCount * frames
                && cargo.animals.Visited == (long)residentCount * frames,
                "300 legacy cargo frames scan both 4096-member resident lists");
            Console.WriteLine("INFO: " + frames + " cargo-only frame samples with " + residentCount
                + " heroes and " + residentCount + " animals: legacy visited "
                + (cargo.heros.Visited + cargo.animals.Visited)
                + " resident entries; linked production capture visited 0");

            var mixed = new FWGameData();
            bridge.Players.Add("mixed", mixed);
            var e1 = Expedition(501, 81, 83, 81, 84, 85);
            e1.animalUidList.AddRange(new long[] { 92, 91, 92, 95 });
            var e2 = Expedition(502, 86, 85, 83);
            e2.animalUidList.AddRange(new long[] { 96, 92 });
            mixed.expeditionSeq.Add(e1);
            mixed.expeditionSeq.Add(e2);
            mixed.heros.AddRange(new[] { Hero(83), Hero(84, 123), Hero(81),
                Hero(86, -1, 100), Hero(85), Hero(999) });
            mixed.animals.AddRange(new[] { Animal(96), Animal(91, -1, 500),
                Animal(92), Animal(95, 6), Animal(999) });
            Reset(mixed);
            var actualMixed = bridge.TestCapture("mixed");
            Check(mixed.heros.Enumerations == 1 && mixed.animals.Enumerations == 1,
                "active mixed passenger manifests scan each needed resident list once");
            Check(actualMixed.Heroes.SequenceEqual(new[] { "hero-83", "hero-81", "hero-85" })
                && actualMixed.Animals.SequenceEqual(new[] { "animal-96", "animal-92" }),
                "repeated UID requests do not duplicate passengers or include grounded/fleet residents");
            Same(Legacy(mixed, "mixed"), actualMixed, "mixed manifests and passenger order");
            Check(bridge.CapturedId(81) && bridge.CapturedId(83) && bridge.CapturedId(92)
                && !bridge.CapturedId(999) && !bridge.CapturedId(84),
                "sampled passenger IDs include valid transit residents only");
            Check(!ReferenceEquals(bridge.Cached(501), bridge.Cached(502))
                && bridge.Cached(81).Encoded == "hero-81",
                "all requested native payloads retain distinct cached identities");
            var mixedCached = bridge.Cached(81);
            NativeWire.CacheHits = 0;
            var repeatedMixed = bridge.TestCapture("mixed");
            Same(actualMixed, repeatedMixed, "repeated mixed manifest");
            Check(ReferenceEquals(bridge.Cached(81), mixedCached) && NativeWire.CacheHits > 0,
                "repeated passenger snapshots keep native cached encoded instances");
            FWHeroBean changed = mixed.heros.Single(hero => hero.getUID() == 81);
            changed.Payload = "hero-81-new";
            var updatedMixed = bridge.TestCapture("mixed");
            Check(updatedMixed.Heroes.Contains("hero-81-new") && !ReferenceEquals(bridge.Cached(81), mixedCached),
                "changed passenger payload is still recaptured and encoded");
            Same(Legacy(mixed, "mixed"), updatedMixed, "changed passenger output");

            // Cargo can travel with only heroes or only animals; the other
            // large resident collection must not be scanned on either path.
            e1.animalUidList.Clear();
            e2.animalUidList.Clear();
            Reset(mixed);
            var heroOnly = bridge.TestCapture("mixed");
            Check(mixed.heros.Enumerations == 1 && mixed.animals.Enumerations == 0,
                "hero-only expedition never walks animal residents");
            Same(Legacy(mixed, "mixed"), heroOnly, "hero-only manifest");
            e1.roleUidList.Clear(); e2.roleUidList.Clear();
            e1.animalUidList.Add(96);
            Reset(mixed);
            var animalOnly = bridge.TestCapture("mixed");
            Check(mixed.heros.Enumerations == 0 && mixed.animals.Enumerations == 1,
                "animal-only expedition never walks hero residents");
            Same(Legacy(mixed, "mixed"), animalOnly, "animal-only manifest");
            e1.animalUidList.Clear();
            Reset(mixed);
            var becomesCargo = bridge.TestCapture("mixed");
            Check(becomesCargo.Heroes.Count == 0 && becomesCargo.Animals.Count == 0
                && mixed.heros.Enumerations == 0 && mixed.animals.Enumerations == 0,
                "transition from passenger transport to cargo-only drops all prior passenger content");

            // Two player manifests never reuse another player's lists or
            // identity; the source cache remains the same GameBridge cache.
            bridge.BeginFrame();
            var playerOne = bridge.TestCapture("cargo");
            var playerTwo = bridge.TestCapture("mixed");
            Check(playerOne.PlayerId == "cargo" && playerTwo.PlayerId == "mixed"
                && playerOne.Expeditions.Count == 6 && playerTwo.Expeditions.Count == 2,
                "multiple players retain independent manifests on the same galaxy sample");
            Check(bridge.CapturedCount == 8,
                "two cargo-only players capture eight distinct expedition identities");

            Console.WriteLine("PASS: " + checks +
                " linked production fleet transit capture checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
