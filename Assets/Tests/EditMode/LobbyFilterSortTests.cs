using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>Filtrado y ordenación de la tabla del navegador. Funciones puras.</summary>
    public sealed class LobbyFilterSortTests
    {
        private const string ClientVersion = "1.2.3";

        private static Lobby Make(string id, int players = 1, int maxPlayers = 8,
            string version = ClientVersion, bool password = false,
            LobbyPrivacy privacy = LobbyPrivacy.Public, int ping = 40,
            string map = "Level 0", string region = "EU-West", string name = null)
        {
            Assert.IsTrue(Lobby.TryCreate(id, name ?? ("Server " + id), version, players, maxPlayers,
                map, region, ping, privacy, password, "10.0.0.1", 7778, 1000d, 30f,
                LobbyStatus.Waiting, out Lobby lobby));
            return lobby;
        }

        private static LobbyList ListOf(params Lobby[] lobbies) => LobbyList.Create(lobbies, 1000d);

        private static List<string> Ids(List<Lobby> lobbies)
        {
            var ids = new List<string>(lobbies.Count);
            for (int i = 0; i < lobbies.Count; i++) ids.Add(lobbies[i].Id.Value);
            return ids;
        }

        // ─── Filtro ───

        [Test]
        public void EmptyFilterKeepsEverything()
        {
            var filter = new LobbyFilter { ClientVersion = ClientVersion };
            LobbyList list = ListOf(Make("a"), Make("b", players: 8, maxPlayers: 8), Make("c", password: true));
            Assert.AreEqual(3, filter.Apply(list).Count);
        }

        [Test]
        public void HideFullDropsOnlyFullServers()
        {
            var filter = new LobbyFilter { HideFull = true };
            LobbyList list = ListOf(Make("free", players: 7, maxPlayers: 8), Make("full", players: 8, maxPlayers: 8));
            CollectionAssert.AreEqual(new[] { "free" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void HideEmptyDropsOnlyEmptyServers()
        {
            var filter = new LobbyFilter { HideEmpty = true };
            LobbyList list = ListOf(Make("empty", players: 0), Make("one", players: 1));
            CollectionAssert.AreEqual(new[] { "one" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void HidePasswordProtectedDropsLockedServers()
        {
            var filter = new LobbyFilter { HidePasswordProtected = true };
            LobbyList list = ListOf(Make("open"), Make("locked", password: true));
            CollectionAssert.AreEqual(new[] { "open" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void HideIncompatibleNeedsAClientVersionToMeanAnything()
        {
            LobbyList list = ListOf(Make("same"), Make("old", version: "0.9"));

            var withoutVersion = new LobbyFilter { HideIncompatibleVersion = true, ClientVersion = "" };
            Assert.AreEqual(2, withoutVersion.Apply(list).Count,
                "sin versión de cliente no se puede declarar nada incompatible");

            var withVersion = new LobbyFilter { HideIncompatibleVersion = true, ClientVersion = ClientVersion };
            CollectionAssert.AreEqual(new[] { "same" }, Ids(withVersion.Apply(list)));
        }

        [Test]
        public void HideNonPublicDropsFriendsOnlyAndPrivate()
        {
            var filter = new LobbyFilter { HideNonPublic = true };
            LobbyList list = ListOf(
                Make("pub"),
                Make("friends", privacy: LobbyPrivacy.FriendsOnly),
                Make("priv", privacy: LobbyPrivacy.Private));
            CollectionAssert.AreEqual(new[] { "pub" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void MaxPingKeepsServersWithoutAMeasuredPing()
        {
            // Un servidor sin ping medido no ha hecho nada mal; esconderlo lo castigaría por no
            // haber contestado todavía.
            var filter = new LobbyFilter { MaxPingMs = 100 };
            LobbyList list = ListOf(
                Make("fast", ping: 30),
                Make("slow", ping: 300),
                Make("unknown", ping: Lobby.UnknownPing));
            CollectionAssert.AreEquivalent(new[] { "fast", "unknown" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void RegionAndMapAreExactAndCaseInsensitive()
        {
            LobbyList list = ListOf(
                Make("eu", region: "EU-West", map: "Level 0"),
                Make("na", region: "NA-East", map: "Level 37"));

            var byRegion = new LobbyFilter { Region = "eu-west" };
            CollectionAssert.AreEqual(new[] { "eu" }, Ids(byRegion.Apply(list)));

            var byMap = new LobbyFilter { Map = "level 37" };
            CollectionAssert.AreEqual(new[] { "na" }, Ids(byMap.Apply(list)));

            var byPartialRegion = new LobbyFilter { Region = "EU" };
            Assert.AreEqual(0, byPartialRegion.Apply(list).Count, "región es exacta, no subcadena");
        }

        [Test]
        public void SearchTextMatchesNameOrMapIgnoringCase()
        {
            LobbyList list = ListOf(
                Make("a", name: "Almond Water Lounge", map: "Level 0"),
                Make("b", name: "Pool Rooms", map: "Level 37"),
                Make("c", name: "Hub", map: "Poolrooms Annex"));

            var filter = new LobbyFilter { SearchText = "  pool " };
            CollectionAssert.AreEquivalent(new[] { "b", "c" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void FiltersCombineAsAnd()
        {
            var filter = new LobbyFilter
            {
                ClientVersion = ClientVersion,
                HideFull = true,
                HidePasswordProtected = true,
                HideIncompatibleVersion = true,
                MaxPingMs = 100,
            };

            LobbyList list = ListOf(
                Make("good", players: 2, ping: 50),
                Make("full", players: 8, maxPlayers: 8, ping: 50),
                Make("locked", password: true, ping: 50),
                Make("old", version: "0.9", ping: 50),
                Make("laggy", ping: 400));

            CollectionAssert.AreEqual(new[] { "good" }, Ids(filter.Apply(list)));
        }

        [Test]
        public void FilterDoesNotMutateTheSnapshot()
        {
            LobbyList list = ListOf(Make("a"), Make("full", players: 8, maxPlayers: 8));
            new LobbyFilter { HideFull = true }.Apply(list);
            Assert.AreEqual(2, list.Count);
        }

        // ─── Orden ───

        [Test]
        public void SortsByPingAscendingAndSinksUnknownPings()
        {
            var sort = new LobbySort(LobbySortKey.Ping);
            List<Lobby> sorted = sort.Sorted(new[]
            {
                Make("slow", ping: 300),
                Make("unknown", ping: Lobby.UnknownPing),
                Make("fast", ping: 20),
            });
            CollectionAssert.AreEqual(new[] { "fast", "slow", "unknown" }, Ids(sorted));
        }

        [Test]
        public void UnknownPingStaysLastWhenDescending()
        {
            var sort = new LobbySort(LobbySortKey.Ping, descending: true);
            List<Lobby> sorted = sort.Sorted(new[]
            {
                Make("fast", ping: 20),
                Make("unknown", ping: Lobby.UnknownPing),
                Make("slow", ping: 300),
            });
            CollectionAssert.AreEqual(new[] { "slow", "fast", "unknown" }, Ids(sorted));
        }

        [Test]
        public void SortsByPlayersAndFreeSlots()
        {
            var byPlayers = new LobbySort(LobbySortKey.Players, descending: true);
            CollectionAssert.AreEqual(new[] { "b", "a" }, Ids(byPlayers.Sorted(new[]
            {
                Make("a", players: 1), Make("b", players: 6),
            })));

            var byFree = new LobbySort(LobbySortKey.FreeSlots, descending: true);
            CollectionAssert.AreEqual(new[] { "a", "b" }, Ids(byFree.Sorted(new[]
            {
                Make("a", players: 1, maxPlayers: 16), Make("b", players: 6, maxPlayers: 8),
            })));
        }

        [Test]
        public void SortsByNameMapAndRegionIgnoringCase()
        {
            var byName = new LobbySort(LobbySortKey.Name);
            CollectionAssert.AreEqual(new[] { "b", "a" }, Ids(byName.Sorted(new[]
            {
                Make("a", name: "zeta"), Make("b", name: "Alpha"),
            })));

            var byMap = new LobbySort(LobbySortKey.Map);
            CollectionAssert.AreEqual(new[] { "b", "a" }, Ids(byMap.Sorted(new[]
            {
                Make("a", map: "Level 9"), Make("b", map: "Level 0"),
            })));

            var byRegion = new LobbySort(LobbySortKey.Region);
            CollectionAssert.AreEqual(new[] { "b", "a" }, Ids(byRegion.Sorted(new[]
            {
                Make("a", region: "NA-East"), Make("b", region: "EU-West"),
            })));
        }

        [Test]
        public void TiesBreakByIdSoTheOrderIsStableAcrossRefreshes()
        {
            // List.Sort no es estable: sin desempate, dos refrescos con los mismos datos pueden
            // pintar las filas en distinto orden y la fila bajo el cursor cambiaría sola.
            var sort = new LobbySort(LobbySortKey.Ping);
            List<Lobby> first = sort.Sorted(new[]
            {
                Make("ccc", ping: 50), Make("aaa", ping: 50), Make("bbb", ping: 50),
            });
            List<Lobby> second = sort.Sorted(new[]
            {
                Make("bbb", ping: 50), Make("ccc", ping: 50), Make("aaa", ping: 50),
            });

            CollectionAssert.AreEqual(new[] { "aaa", "bbb", "ccc" }, Ids(first));
            CollectionAssert.AreEqual(Ids(first), Ids(second));
        }

        [Test]
        public void SortingAnEmptyOrSingleListIsSafe()
        {
            var sort = new LobbySort(LobbySortKey.Name);
            Assert.AreEqual(0, sort.Sorted(new Lobby[0]).Count);
            Assert.AreEqual(0, sort.Sorted(null).Count);
            Assert.AreEqual(1, sort.Sorted(new[] { Make("a") }).Count);
        }
    }
}
