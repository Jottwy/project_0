using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El lado del HOST: quién anuncia la partida, cuándo, y —lo que de verdad importa— cuándo
    /// deja de anunciarla. Un lobby fantasma es un servidor en la lista al que nadie puede entrar,
    /// y es el fallo más caro de este sistema porque el jugador lo descubre a los 15 s de espera.
    ///
    /// Todo sin Steam: la costura es <see cref="ISteamLobbyHost"/>.
    /// </summary>
    public sealed class SteamLobbyPublisherTests
    {
        private const string Wire = "52";

        /// Lobby de Steam de mentira. Cuenta creaciones porque "no publicar dos veces" es una de
        /// las reglas que hay que probar.
        private sealed class FakeSteamHost : ISteamLobbyHost
        {
            public bool Available = true;

            /// Steam tarda en crear el lobby: el primer EnsureLobby devuelve false, como el real.
            public bool CreateInstantly = true;

            public int EnsureCalls;
            public int CreateCount;
            public int CloseCount;
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public string LastIp;
            public int LastPort;

            public bool IsAvailable => Available;
            public bool HasLobby { get; private set; }

            public bool EnsureLobby(string ip, int port)
            {
                EnsureCalls++;
                LastIp = ip;
                LastPort = port;
                if (!Available) return false;

                if (HasLobby) return true;
                if (!CreateInstantly)
                {
                    CreateInstantly = true; // el siguiente intento ya lo encuentra hecho
                    return false;
                }

                HasLobby = true;
                CreateCount++;
                return true;
            }

            public bool SetData(string key, string value)
            {
                if (!HasLobby) return false;
                Data[key] = value;
                return true;
            }

            public void CloseLobby()
            {
                if (!HasLobby) return;
                HasLobby = false;
                CloseCount++;
                Data.Clear();
            }

            /// Steam se lleva el lobby por su cuenta (el cliente se cerró, la red se fue).
            public void DropLobbyBehindOurBack()
            {
                HasLobby = false;
                Data.Clear();
            }
        }

        private static LobbyPublication Publication(string ip = "192.168.1.40", int port = 7791,
            int maxPlayers = 50) =>
            new LobbyPublication("Joel", Wire, maxPlayers, "STP_Showcase", "Unknown",
                LobbyPrivacy.Public, false, new LobbyEndpoint(ip, port));

        // ─── Publicación ───

        [Test]
        public void PublishesOnceWithTheRealEndpoint()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);

            Assert.IsTrue(publisher.Publish(Publication(port: 7791), 1, LobbyStatus.Waiting, 1000d));

            Assert.IsTrue(publisher.IsPublishing);
            Assert.AreEqual(1, host.CreateCount);
            Assert.AreEqual("192.168.1.40", host.LastIp);
            Assert.AreEqual(7791, host.LastPort, "el puerto anunciado es el REALMENTE elegido");
            Assert.AreEqual("192.168.1.40", host.Data[SteamLobbyKeys.ConnectIp]);
            Assert.AreEqual("7791", host.Data[SteamLobbyKeys.ConnectPort]);
        }

        [Test]
        public void PublishesEverythingTheBrowserNeedsToDecide()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 3, LobbyStatus.InProgress, 1700000000d);

            Assert.AreEqual(SteamLobbyKeys.GameValue, host.Data[SteamLobbyKeys.Game],
                "sin la marca de juego, la consulta traería los lobbies de cualquiera con el mismo App ID");
            Assert.AreEqual(Wire, host.Data[SteamLobbyKeys.WireVersion]);
            Assert.AreEqual("Joel", host.Data[SteamLobbyKeys.Name]);
            Assert.AreEqual("3", host.Data[SteamLobbyKeys.Players]);
            Assert.AreEqual("50", host.Data[SteamLobbyKeys.MaxPlayers]);
            Assert.AreEqual("STP_Showcase", host.Data[SteamLobbyKeys.Map]);
            Assert.AreEqual("ingame", host.Data[SteamLobbyKeys.State]);
            Assert.AreEqual("1700000000", host.Data[SteamLobbyKeys.AnnouncedAt]);
        }

        [Test]
        public void PublishingTwiceDoesNotCreateASecondLobby()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);

            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);
            publisher.Publish(Publication(), 4, LobbyStatus.InProgress, 1001d);

            Assert.AreEqual(1, host.CreateCount);
            Assert.AreEqual(1, publisher.PublishCount, "la segunda es una actualización, no un lobby nuevo");
            Assert.AreEqual("4", host.Data[SteamLobbyKeys.Players], "y sí actualiza los datos");
        }

        [Test]
        public void ARefusedPublicationLeavesNothingHalfAnnounced()
        {
            var host = new FakeSteamHost { Available = false };
            var publisher = new SteamLobbyPublisher(host);

            Assert.IsFalse(publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d));
            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(0, host.CreateCount);
        }

        [Test]
        public void RejectsAPublicationWithoutAnyWayIn()
        {
            // ADR-117 cambió la regla de «endpoint válido» a «alguna vía de entrada», pero NO la
            // relajó: sin endpoint y sin relay se sigue sin publicar, que es la defensa de ADR-112.
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);

            Assert.IsFalse(publisher.Publish(Publication(port: 0), 1, LobbyStatus.Waiting, 1000d));
            Assert.IsFalse(publisher.Publish(Publication(ip: ""), 1, LobbyStatus.Waiting, 1000d));
            Assert.IsFalse(publisher.Publish(Publication(maxPlayers: 0), 1, LobbyStatus.Waiting, 1000d));
            Assert.AreEqual(0, host.EnsureCalls, "no se crea un lobby para anunciar una dirección muerta");
        }

        [Test]
        public void PublishesARelayOnlyLobbyWithNoDirectEndpoint()
        {
            // ADR-117 D7: es el host sin UPnP ni reenvío de puertos — o sea, el caso normal, y el
            // que el playtest del 2026-09-02 dejó fuera de la partida.
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);

            var relayOnly = new LobbyPublication("A12ex", "55", 50, "Level0", "Unknown",
                LobbyPrivacy.Public, false, LobbyEndpoint.None, Lobby.DefaultTtlSeconds, true);

            Assert.IsTrue(publisher.Publish(relayOnly, 1, LobbyStatus.Waiting, 1000d));
            Assert.AreEqual(1, host.EnsureCalls);
            Assert.AreEqual("", host.Data[SteamLobbyKeys.ConnectIp],
                "sin endpoint directo la clave va vacía, no con un relleno");
            Assert.AreEqual("", host.Data[SteamLobbyKeys.ConnectPort]);
        }

        // ─── Latido ───

        [Test]
        public void TouchUpdatesPlayersAndState()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);

            publisher.Touch(6, LobbyStatus.InProgress, 1010d);

            Assert.AreEqual("6", host.Data[SteamLobbyKeys.Players]);
            Assert.AreEqual("ingame", host.Data[SteamLobbyKeys.State]);
            Assert.AreEqual("1010", host.Data[SteamLobbyKeys.AnnouncedAt]);
            Assert.AreEqual(1, publisher.TouchCount);
        }

        [Test]
        public void TouchAfterWithdrawIsImpossible()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);
            publisher.Withdraw();

            publisher.Touch(6, LobbyStatus.InProgress, 1010d);

            Assert.AreEqual(0, publisher.TouchCount, "un anuncio retirado no se resucita con un latido");
            Assert.IsFalse(host.HasLobby);
            Assert.AreEqual(0, host.Data.Count);
        }

        [Test]
        public void PlayerCountIsClampedIntoCapacity()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(maxPlayers: 8), 99, LobbyStatus.Waiting, 1000d);

            Assert.AreEqual("8", host.Data[SteamLobbyKeys.Players]);
        }

        [Test]
        public void ALobbyStolenBySteamStopsCountingAsPublished()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);

            host.DropLobbyBehindOurBack();

            Assert.IsFalse(publisher.IsPublishing, "si Steam se lo llevó, no estamos anunciando nada");
        }

        // ─── Retirada ───

        [Test]
        public void WithdrawClosesTheLobbyAndIsIdempotent()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 1, LobbyStatus.Waiting, 1000d);

            publisher.Withdraw();
            publisher.Withdraw();

            Assert.AreEqual(1, host.CloseCount);
            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(LobbyId.None, publisher.PublishedId);
        }

        // ─── El conductor: quién decide CUÁNDO ───

        private static HostAnnouncementState State(bool isHost = true, bool established = true,
            int players = 1, LobbyStatus status = LobbyStatus.InProgress, int port = 7791) =>
            new HostAnnouncementState(isHost, established, new LobbyEndpoint("192.168.1.40", port),
                "Joel", Wire, players, 50, "STP_Showcase", status);

        [Test]
        public void DoesNotPublishIfWeAreNotTheHost()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);

            driver.Update(State(isHost: false), 1000d);

            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(0, host.EnsureCalls);
        }

        [Test]
        public void DoesNotPublishBeforeTheSessionIsEstablished()
        {
            // Antes de Connected el endpoint todavía puede cambiar: anunciarlo sería publicar un
            // puerto que puede acabar siendo otro.
            var host = new FakeSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(established: false), 1000d);

            Assert.AreEqual(0, host.EnsureCalls);
        }

        [Test]
        public void DoesNotPublishWithoutAResolvedEndpoint()
        {
            var host = new FakeSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(port: 0), 1000d);

            Assert.AreEqual(0, host.EnsureCalls);
        }

        [Test]
        public void PublishesOnceWhenTheHostReachesTheWorld()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);

            driver.Update(State(), 1000d);
            driver.Update(State(), 1001d);
            driver.Update(State(), 1002d);

            Assert.IsTrue(publisher.IsPublishing);
            Assert.AreEqual(1, host.CreateCount, "tres frames de host no son tres lobbies");
        }

        [Test]
        public void RetriesWhenSteamHasNotFinishedCreatingTheLobby()
        {
            // La creación es asíncrona: el primer intento devuelve false y el conductor reintenta.
            var host = new FakeSteamHost { CreateInstantly = false };
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);

            driver.Update(State(), 1000d);
            Assert.IsFalse(publisher.IsPublishing);

            driver.Update(State(), 1000.5d);
            Assert.IsFalse(publisher.IsPublishing, "no se martillea a Steam en cada frame");

            driver.Update(State(), 1000d + HostAnnouncementDriver.RetryIntervalSeconds);
            Assert.IsTrue(publisher.IsPublishing);
            Assert.AreEqual(1, host.CreateCount);
        }

        [Test]
        public void HeartbeatsOnItsOwnCadenceAndNotEveryFrame()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);

            driver.Update(State(players: 1), 1000d);
            driver.Update(State(players: 2), 1001d);
            Assert.AreEqual(0, publisher.TouchCount);
            Assert.AreEqual("1", host.Data[SteamLobbyKeys.Players]);

            driver.Update(State(players: 2), 1000d + HostAnnouncementDriver.TouchIntervalSeconds);
            Assert.AreEqual(1, publisher.TouchCount);
            Assert.AreEqual("2", host.Data[SteamLobbyKeys.Players]);
        }

        [Test]
        public void WithdrawsWhenTheSessionEnds()
        {
            // Cubre TODOS los finales: Quit to Menu, backend muerto, desconexión. El conductor no
            // distingue ninguno — sólo sabe que ya no somos un host con sesión viva.
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);
            driver.Update(State(), 1000d);
            Assert.IsTrue(publisher.IsPublishing);

            driver.Update(State(isHost: false, established: false), 1005d);

            Assert.IsFalse(publisher.IsPublishing);
            Assert.AreEqual(1, host.CloseCount);
            Assert.AreEqual(1, driver.WithdrawCount);
        }

        [Test]
        public void WithdrawHappensOnlyOnceNoMatterHowManyFramesPass()
        {
            var host = new FakeSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));
            driver.Update(State(), 1000d);

            driver.Update(State(isHost: false), 1005d);
            driver.Update(State(isHost: false), 1006d);
            driver.Update(State(isHost: false), 1007d);

            Assert.AreEqual(1, host.CloseCount);
            Assert.AreEqual(1, driver.WithdrawCount);
        }

        [Test]
        public void HostingAgainAfterQuittingAnnouncesAgain()
        {
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            var driver = new HostAnnouncementDriver(publisher);

            driver.Update(State(), 1000d);
            driver.Update(State(isHost: false), 1005d);
            driver.Update(State(port: 7800), 1010d);

            Assert.IsTrue(publisher.IsPublishing);
            Assert.AreEqual(2, host.CreateCount);
            Assert.AreEqual(7800, host.LastPort, "la segunda partida anuncia SU puerto, no el de la anterior");
        }

        [Test]
        public void ForceWithdrawCoversTheProcessShuttingDown()
        {
            var host = new FakeSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));
            driver.Update(State(), 1000d);

            driver.ForceWithdraw();
            driver.ForceWithdraw();

            Assert.AreEqual(1, host.CloseCount);
        }

        [Test]
        public void WhatTheHostPublishesIsWhatTheBrowserReads()
        {
            // El circuito entero contra el contrato de metadatos: si una clave se renombra en un
            // lado, este test cae antes que un playtest a dos máquinas.
            var host = new FakeSteamHost();
            var publisher = new SteamLobbyPublisher(host);
            publisher.Publish(Publication(), 3, LobbyStatus.InProgress, 1700000000d);

            var record = new SteamLobbyRecord
            {
                Id = 4242UL,
                ConnectIp = host.Data[SteamLobbyKeys.ConnectIp],
                ConnectPort = host.Data[SteamLobbyKeys.ConnectPort],
                Name = host.Data[SteamLobbyKeys.Name],
                WireVersion = host.Data[SteamLobbyKeys.WireVersion],
                Players = host.Data[SteamLobbyKeys.Players],
                MaxPlayers = host.Data[SteamLobbyKeys.MaxPlayers],
                Map = host.Data[SteamLobbyKeys.Map],
                State = host.Data[SteamLobbyKeys.State],
                AnnouncedAt = host.Data[SteamLobbyKeys.AnnouncedAt],
            };

            Assert.IsTrue(SteamLobbyMapper.TryMap(record, 2000d, out Lobby lobby));
            Assert.AreEqual("Joel", lobby.Name);
            Assert.AreEqual(3, lobby.Players);
            Assert.AreEqual(50, lobby.MaxPlayers);
            Assert.AreEqual(LobbyStatus.InProgress, lobby.Status);
            Assert.AreEqual("192.168.1.40", lobby.Endpoint.Host);
            Assert.AreEqual(7791, lobby.Endpoint.Port);
            Assert.AreEqual(LobbyJoinability.Joinable, lobby.EvaluateJoinability(Wire, 2000d));
        }

        // ─── El fantasma de la creación a medias ───

        /// <summary>
        /// Steam de verdad: `EnsureLobby` dispara la creación y devuelve false; el lobby aparece
        /// más tarde, por su cuenta, sin que nadie vuelva a llamar. <see cref="FakeSteamHost"/> no
        /// sirve para esto porque cede al SEGUNDO intento, y el fallo que se prueba aquí ocurre
        /// justo cuando NO hay segundo intento.
        /// </summary>
        private sealed class AsyncSteamHost : ISteamLobbyHost
        {
            public int CloseCount;
            public bool HasLobby { get; private set; }
            public bool IsAvailable => true;

            public bool EnsureLobby(string ip, int port) => HasLobby;

            /// La continuación de `CreateLobbyAsync` aterrizando: el lobby ya existe en Steam.
            public void CreationLands() => HasLobby = true;

            public bool SetData(string key, string value) => HasLobby;

            public void CloseLobby()
            {
                if (!HasLobby) return;
                HasLobby = false;
                CloseCount++;
            }
        }

        [Test]
        public void ALobbyThatFinishesCreatingAfterTheSessionEndedIsClosed()
        {
            // El fantasma más caro y el más difícil de ver: `Publish` devolvió false (Steam seguía
            // creando), así que `IsPublishing` nunca fue cierto y la rama de retirada no se
            // disparaba. Un par de segundos después el lobby nacía —público, joinable, apuntando a
            // un endpoint ya muerto— y NADIE lo cerraba hasta cerrar el proceso.
            var host = new AsyncSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(), 1000d);          // publica: Steam dice "todavía no"
            host.CreationLands();                   // la creación aterriza, tarde
            driver.Update(State(isHost: false), 1005d); // y para entonces la sesión ya terminó

            Assert.IsFalse(host.HasLobby, "el lobby nacido tarde tiene que morir en el teardown");
            Assert.AreEqual(1, host.CloseCount);
            Assert.AreEqual(1, driver.WithdrawCount);
        }

        [Test]
        public void ForceWithdrawAlsoClosesALobbyThatArrivedLate()
        {
            // Mismo agujero por la puerta de OnApplicationQuit/OnDestroy.
            var host = new AsyncSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(), 1000d);
            host.CreationLands();
            driver.ForceWithdraw();

            Assert.IsFalse(host.HasLobby);
            Assert.AreEqual(1, host.CloseCount);
        }

        [Test]
        public void APublishThatNeverStartedDoesNotCountAsAWithdrawal()
        {
            // La otra cara: sin intento de publicación no hay nada que retirar, y el conductor no
            // puede inventarse un `Withdraw` por frame mientras se está en el menú.
            var host = new AsyncSteamHost();
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(isHost: false), 1000d);
            driver.Update(State(isHost: false), 1001d);
            driver.ForceWithdraw();

            Assert.AreEqual(0, host.CloseCount);
            Assert.AreEqual(0, driver.WithdrawCount);
        }

        [Test]
        public void ThePendingFlagClearsOnceThePublishSucceeds()
        {
            // Si `_publishPending` se quedara pegado, cada vuelta al menú contaría una retirada de
            // más y el contador dejaría de servir como testigo.
            var host = new FakeSteamHost { CreateInstantly = false };
            var driver = new HostAnnouncementDriver(new SteamLobbyPublisher(host));

            driver.Update(State(), 1000d);                                            // pending
            driver.Update(State(), 1000d + HostAnnouncementDriver.RetryIntervalSeconds); // publica
            driver.Update(State(), 1003d);                                            // ya anunciado
            driver.Update(State(isHost: false), 1005d);                               // teardown
            driver.Update(State(isHost: false), 1006d);

            Assert.AreEqual(1, host.CloseCount);
            Assert.AreEqual(1, driver.WithdrawCount);
        }
    }
}
