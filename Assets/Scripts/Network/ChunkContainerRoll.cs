using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Net
{
    /// <summary>Qué MUEBLE es un contenedor del mundo. Sale del PAPEL del espacio donde cae, así
    /// que no viaja por el cable: el cliente lo deriva de la posición igual que ya deriva el tono
    /// de una pared. `None` significa que ese papel no admite contenedores.</summary>
    public enum WorldContainerProp : byte
    {
        None = 0,
        /// <summary>Callejón sin salida — lo que alguien dejó apilado en un rincón.</summary>
        Crate = 1,
        /// <summary>Oficina.</summary>
        Desk = 2,
        /// <summary>Servicio / almacén.</summary>
        Locker = 3,
        /// <summary>Nave — estantería industrial.</summary>
        Shelf = 4,
    }

    /// <summary>
    /// PURO y determinista, mismo contrato que <see cref="ChunkLootRoll"/>: sin acceso a la escena
    /// de Unity, así que se prueba headless. Dado (worldSeed, cx, cz) siempre contesta el MISMO
    /// contenedor.
    ///
    /// Cierra la cadena que faltaba —worldgen, prop, contenedor, tabla de loot, loot— y la cierra
    /// SIN INVENTAR NADA:
    ///
    ///  · el SITIO lo decide el papel del espacio de WG3, que el cliente ya sabe leer
    ///    (`Wg3ChunkStreamer.TryGetStyle`), igual que ya decide qué loot suelto cae (ADR-108 D4);
    ///  · el PROP se deriva de ese mismo papel, así que no hay campo nuevo en el cable;
    ///  · el CONTENEDOR es un COFRE de ADR-028 (`is_chest`), que ya trae del servidor todo lo que
    ///    un contenedor necesita y este trabajo no tiene que construir: qué queda dentro, quién
    ///    saqueó, la guarda contra dos jugadores abriendo a la vez, la persistencia, el despawn al
    ///    vaciarse y la guarda que impide re-sembrar donde ya hay uno;
    ///  · el CONTENIDO sale del mismo catálogo que las cachés del suelo.
    ///
    /// LO QUE ESTE SORTEO NO HACE, A PROPÓSITO: agua de almendras. Las botellas de toda la
    /// partida (una por cofre, `ChestCount` en <c>StpChestSpawner</c>) salen sólo de ahí, y esa cuenta está medida contra
    /// los drenajes de sed. Un contenedor que sirviera agua movería un balance de supervivencia
    /// que ya está validado en partida; éste sirve lo mismo que el suelo, en otro envase.
    /// </summary>
    public static class ChunkContainerRoll
    {
        /// <summary>Probabilidad de que un chunk tenga un contenedor, ANTES de mirar el papel.
        /// TODO(balance): placeholder de primer playtest. Con papeles admisibles en algo más de la
        /// mitad del mundo (oficina 31 %, servicio 16 %, callejón 10 %, nave 5 % — reparto medido
        /// en la enmienda 2 de ADR-103), esto deja del orden de un mueble por anillo 3×3. El loot
        /// suelto NO se toca: esto es ADITIVO y por eso arranca bajo.</summary>
        public const float ContainerChance = 0.10f;

        /// <summary>Uno por chunk como máximo. No es una limitación técnica: un chunk con dos
        /// muebles pide decidir si se agrupan o se reparten, y ésa es una decisión de contenido
        /// que no hace falta tomar para que la cadena exista.</summary>
        public const int MaxContainersPerChunk = 1;

        /// <summary>Sal propia — el contenedor no puede quedar correlacionado con la caché suelta
        /// del mismo chunk, o el mundo pondría el mueble y el montón siempre en la misma esquina.
        /// Mismo motivo por el que `ItemSalt` y `CarrySalt` son distintas.</summary>
        private const ulong ContainerSalt = 0xC017A11E02UL;

        /// <summary>Cuántos objetos lleva dentro, por encima del reparto de una caché del suelo.
        /// Un mueble que se abre para encontrar exactamente lo mismo que hay tirado por el suelo
        /// no compensa el gesto de abrirlo; es la ÚNICA ventaja que se le da, y es de una unidad.
        /// </summary>
        public const int ContainerBonusItems = 1;

        /// <summary>El mueble que le toca a cada papel, y los tres que NO llevan ninguno.
        ///
        /// Espina, pasillo y escalera quedan fuera por la misma razón por la que la escalera ya
        /// tiene `itemCacheChance = 0` (ADR-108 enmienda 4): son sitio de PASO. Un mueble ahí
        /// estorba la circulación, que es lo mismo que protege la puerta de construcción de
        /// ADR-108 D6 al dejar construir sólo en servicio, almacén y callejón.</summary>
        public static WorldContainerProp PropForStyle(byte style)
        {
            switch (style)
            {
                case 0: return WorldContainerProp.Desk;   // oficina
                case 3: return WorldContainerProp.Shelf;  // nave
                case 4: return WorldContainerProp.Locker; // servicio / almacén
                case 5: return WorldContainerProp.Crate;  // callejón sin salida
                default: return WorldContainerProp.None;  // 1 espina, 2 pasillo, 6 escalera
            }
        }

        /// <summary>Un contenedor del mundo, en coordenadas normalizadas del chunk igual que
        /// <see cref="ChunkLootRoll.Entry"/> — quien lo coloca resuelve (u,v) a mundo con el mismo
        /// rayo de andabilidad que ya usa el loot suelto.</summary>
        public readonly struct Entry
        {
            public readonly WorldContainerProp Prop;
            public readonly float U;
            public readonly float V;
            public readonly float Rotation;
            /// <summary>Nombres de definición STP, ya sorteados. Los resuelve a ids quien coloca,
            /// exactamente como hace el loot suelto.</summary>
            public readonly List<string> Contents;

            public Entry(WorldContainerProp prop, float u, float v, float rotation,
                List<string> contents)
            {
                Prop = prop; U = u; V = v; Rotation = rotation; Contents = contents;
            }
        }

        /// <summary>
        /// El contenedor de este chunk, si lo hay.
        ///
        /// El centro se sortea PRIMERO, igual que en <see cref="ChunkLootRoll.RollItemsByStyle"/> y
        /// por el mismo motivo: hay que saber dónde cae para poder preguntar qué sitio es antes de
        /// decidir si lo hay.
        ///
        /// <paramref name="styleAt"/> devuelve nulo cuando ahí todavía no hay geometría montada, y
        /// eso NO es "aquí no hay contenedor": <paramref name="spaceKnown"/> sale en falso y la
        /// columna se queda sin sellar para reintentarla más tarde. Distinguir "todavía no" de
        /// "aquí no" es lo que evita que una columna se re-sortee para siempre (ADR-108 enm. 4).
        /// </summary>
        public static bool RollContainerByStyle(
            long worldSeed, int cx, int cz,
            Func<float, float, byte?> styleAt,
            Func<byte, ZoneLootProfile> profileForStyle,
            out Entry entry, out bool spaceKnown)
        {
            entry = default;
            var rng = new DeterministicRng(
                ChunkLootRoll.Hash(worldSeed, cx, cz, ContainerSalt));

            ChunkLootRoll.RollCentre(ref rng, out float cu, out float cv);
            byte? found = styleAt(cu, cv);
            spaceKnown = found.HasValue;
            if (!spaceKnown)
                return false;

            WorldContainerProp prop = PropForStyle(found.Value);
            if (prop == WorldContainerProp.None)
                return false; // este papel es sitio de paso

            if (rng.NextFloat() >= ContainerChance)
                return false;

            // El CUARTO eslabón de la cadena: el contenido sale de la tabla de loot del PAPEL, la
            // misma que ya sirve al suelo (`ZoneLootTable.styleProfiles`, ADR-108 D4). Un mueble de
            // servicio pesa material y medicina porque su perfil ya lo dice; no hay tabla nueva.
            ZoneLootProfile profile = profileForStyle(found.Value);
            int count = ChunkLootRoll.RollItemCount(ref rng) + ContainerBonusItems;
            var contents = new List<string>(count);
            for (int i = 0; i < count; i++)
                contents.Add(ChunkLootRoll.RollItemName(ref rng, profile));

            entry = new Entry(prop, cu, cv, rng.NextFloat() * 360f, contents);
            return true;
        }
    }
}
