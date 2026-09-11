//! ADR-140 — el grafo de visibilidad por salas (PVS), **construido pero todavía sin conectar**.
//!
//! # Qué es esto y por qué existe apagado
//!
//! El relay decide hoy a quién manda una pose por DISTANCIA (`AOI_POSE_RADIUS_M`, una esfera de
//! 100 m que no sabe que existen las paredes). En un mundo de habitaciones y pasillos eso incluye a
//! mucha gente que no se puede ver: medido el 10-09, con dos jugadores no importa, pero con
//! cincuenta en la misma sala el relay crece N×(N−1) y es el techo que queda tras ADR-137/138/139.
//!
//! La técnica que lo resuelve es el PVS (*potentially visible set*, Quake 1996): en vez de medir
//! distancia, preguntar **desde tu sala, ¿qué salas se ven?**. Lo caro de aquello era el precálculo
//! offline; aquí no hace falta, porque `RegionPlan` ya trae `spaces`, `links` y `gates` — salas,
//! conexiones y vanos. Este módulo sólo los pasa a una forma consultable.
//!
//! **No lo usa nadie todavía, y es deliberado** (Joel, 10-09: «prepara la infraestructura … para
//! después activar el trigger»). Encenderlo pide dos cosas que NO están hechas:
//!   1. Que el plan sobreviva a la generación. Hoy `Wg3ServedWorld::plan_region` construye el plan,
//!      se lo pasa a `fill_building` y **lo descarta**: en partida el backend tiene geometría, no
//!      salas. Habría que conservar este grafo (que es barato: una caja y una lista de vecinos por
//!      sala) en vez del plan entero.
//!   2. Enchufar la consulta donde ya se decide el relay (`aoi_pose_should_relay`), como una
//!      condición MÁS, nunca como sustituta del radio.
//!
//! # La regla que gobierna el diseño
//!
//! **Se falla siempre del lado de enviar.** Si el PVS se equivoca y dice «no lo ves» cuando sí lo
//! ves, un jugador se vuelve INVISIBLE para otro; si se equivoca al revés, se gastan unos KB. Los
//! dos errores no valen lo mismo, así que:
//!   - la visibilidad se toma por SALTOS en el grafo y no por trazado de rayos (aproximación
//!     generosa: dos salas conectadas se consideran visibles aunque el vano no dé línea directa);
//!   - quien consulte esto debe unirlo con un radio mínimo que se ve SIEMPRE, pase lo que pase.

use super::plan::RegionPlan;

/// Cuántos enlaces de distancia se consideran visibles. 1 = tu sala y las que tocan a la tuya.
///
/// Dos es lo razonable para un pasillo (tú → pasillo → sala de enfrente), y es el valor con el que
/// se medirá cuando esto se encienda. No es una constante de gameplay: es el margen de un
/// sobreconjunto, y subirlo sólo cuesta ancho de banda.
pub const DEFAULT_VISIBILITY_HOPS: usize = 2;

/// El grafo de una región, reducido a lo único que el relay necesita: dónde está cada sala y con
/// quién comunica.
///
/// No guarda el `RegionPlan`: una caja y una lista de vecinos por sala es todo, y así conservarlo en
/// runtime cuesta del orden de decenas de bytes por sala en vez del plan completo.
#[derive(Debug, Clone, Default)]
pub struct VisibilityGraph {
    /// Caja envolvente de cada espacio, en centímetros de mundo: `(x0, z0, x1, z1)`.
    spaces_cm: Vec<(i32, i32, i32, i32)>,
    /// Vecinos de cada espacio, por índice. Simétrico: si `a` está en la lista de `b`, `b` está en
    /// la de `a`.
    neighbours: Vec<Vec<usize>>,
}

impl VisibilityGraph {
    /// Construye el grafo desde un plan ya resuelto.
    ///
    /// Se toma la caja ENVOLVENTE (`rect`) y no la huella exacta (`parts`) a propósito: sobreestimar
    /// es la dirección segura aquí — un punto que cae en el hueco entre partes de una sala deformada
    /// se atribuye igualmente a esa sala, y el resultado es enviar de más, nunca de menos.
    pub fn from_plan(plan: &RegionPlan) -> Self {
        let spaces_cm: Vec<(i32, i32, i32, i32)> = plan
            .spaces
            .iter()
            .map(|s| {
                (
                    s.rect.min_x_cm,
                    s.rect.min_z_cm,
                    s.rect.max_x_cm,
                    s.rect.max_z_cm,
                )
            })
            .collect();

        let mut neighbours = vec![Vec::new(); spaces_cm.len()];
        for link in &plan.links {
            // Un enlace que apunte fuera de rango no debe tumbar la generación del mundo: se ignora.
            if link.a >= spaces_cm.len() || link.b >= spaces_cm.len() || link.a == link.b {
                continue;
            }
            if !neighbours[link.a].contains(&link.b) {
                neighbours[link.a].push(link.b);
            }
            if !neighbours[link.b].contains(&link.a) {
                neighbours[link.b].push(link.a);
            }
        }

        Self {
            spaces_cm,
            neighbours,
        }
    }

    /// Cuántos espacios tiene el grafo.
    pub fn len(&self) -> usize {
        self.spaces_cm.len()
    }

    pub fn is_empty(&self) -> bool {
        self.spaces_cm.is_empty()
    }

    /// En qué espacio cae un punto del mundo, en centímetros. `None` si no cae en ninguno — que es
    /// lo normal en el hueco entre edificios, y el llamante debe tratarlo como «visible para todos»
    /// y no como «invisible».
    pub fn space_at_cm(&self, x_cm: i32, z_cm: i32) -> Option<usize> {
        self.spaces_cm
            .iter()
            .position(|(x0, z0, x1, z1)| x_cm >= *x0 && x_cm <= *x1 && z_cm >= *z0 && z_cm <= *z1)
    }

    /// Los espacios visibles desde `from`, a `hops` enlaces o menos, incluido él mismo.
    ///
    /// Recorrido en anchura sobre un grafo de decenas de nodos: no hay nada que optimizar aquí, y
    /// cuando esto se encienda el resultado se cachea por sala, no se recalcula por par.
    pub fn visible_from(&self, from: usize, hops: usize) -> Vec<usize> {
        if from >= self.spaces_cm.len() {
            return Vec::new();
        }

        let mut seen = vec![false; self.spaces_cm.len()];
        let mut out = Vec::new();
        let mut frontier = vec![from];
        seen[from] = true;
        out.push(from);

        for _ in 0..hops {
            let mut next = Vec::new();
            for &node in &frontier {
                for &n in &self.neighbours[node] {
                    if !seen[n] {
                        seen[n] = true;
                        out.push(n);
                        next.push(n);
                    }
                }
            }
            if next.is_empty() {
                break;
            }
            frontier = next;
        }

        out
    }

    /// ¿Puede alguien en `a_cm` ver a alguien en `b_cm`?
    ///
    /// **Devuelve `true` ante la duda**: si cualquiera de los dos puntos no cae en ningún espacio
    /// conocido, se considera visible. Ver la regla del encabezado — un falso «sí» cuesta bytes, un
    /// falso «no» hace desaparecer a un jugador.
    pub fn can_see(&self, a_cm: (i32, i32), b_cm: (i32, i32), hops: usize) -> bool {
        let (Some(a), Some(b)) = (
            self.space_at_cm(a_cm.0, a_cm.1),
            self.space_at_cm(b_cm.0, b_cm.1),
        ) else {
            return true;
        };
        if a == b {
            return true;
        }
        self.visible_from(a, hops).contains(&b)
    }
}

/// Margen vertical, en centímetros, dentro del cual NO se decide a qué planta pertenece alguien.
///
/// Existe por un fallo conocido y anotado: `storey_of_floor_cm` clasifica una planta ABAJO en la
/// costura de 664 (`STATE.md`, deuda declarada). Un PVS que se equivoque de planta oculta a alguien
/// que está en la tuya, así que en vez de depender de esa función —o de arreglarla desde aquí, que
/// es otra tanda— **cerca de una costura se declara la duda y se ve**.
///
/// 40 cm cubre el canto de losa (`SLAB_CM`) con holgura. Cuesta enviar de más a quien está subiendo
/// una escalera; el error contrario sería un jugador invisible.
pub const STOREY_SEAM_MARGIN_CM: i32 = 40;

/// El grafo de una región COMPLETA: uno por planta, más lo que hace falta para situar una altura.
///
/// Es lo que habría que conservar en `Wg3ServedWorld` para encender el PVS: una caja y unos vecinos
/// por sala, y nada del plan original.
#[derive(Debug, Clone, Default)]
pub struct RegionVisibility {
    /// De abajo arriba, el mismo orden que `RegionBuilding::storeys`.
    storeys: Vec<VisibilityGraph>,
    /// Índice de la CALLE dentro de `storeys` (ADR-130): los sótanos van por debajo.
    ground: usize,
}

impl RegionVisibility {
    pub fn new(storeys: Vec<VisibilityGraph>, ground: usize) -> Self {
        Self { storeys, ground }
    }

    pub fn is_empty(&self) -> bool {
        self.storeys.is_empty()
    }

    /// El grafo de una planta concreta.
    ///
    /// Lo usa el relay para resolver la sala de cada peer UNA vez por ronda en vez de una por par:
    /// con N peers, `can_see` por pareja sería N² consultas al grafo, y la respuesta para un mismo
    /// peer es la misma en todas. Ver `PvsKey` en `network::sync`.
    pub fn storey(&self, index: usize) -> Option<&VisibilityGraph> {
        self.storeys.get(index)
    }

    /// A qué planta pertenece una altura, o `None` si cae **cerca de una costura** y por tanto no se
    /// puede afirmar sin arriesgarse (ver [`STOREY_SEAM_MARGIN_CM`]).
    ///
    /// El índice es relativo a `storeys`, con la calle en `ground`, así que una cota negativa cae en
    /// los sótanos de forma natural.
    pub fn storey_at_cm(&self, y_cm: i32) -> Option<usize> {
        let height = super::plan::STOREY_HEIGHT_CM;
        let within = y_cm.rem_euclid(height);
        if within <= STOREY_SEAM_MARGIN_CM || within >= height - STOREY_SEAM_MARGIN_CM {
            return None; // en la costura: no se decide
        }
        let relative = y_cm.div_euclid(height);
        let index = self.ground as i32 + relative;
        if index < 0 || index as usize >= self.storeys.len() {
            return None;
        }
        Some(index as usize)
    }

    /// **La consulta que usaría el relay.** Devuelve `true` ante cualquier duda.
    ///
    /// Se ven si: están en plantas distintas (se envía y punto — el PVS no opina de verticalidad),
    /// si alguna altura cae en una costura, si la región no tiene grafo, o si el grafo de su planta
    /// dice que sus salas se comunican.
    pub fn can_see(&self, a_cm: (i32, i32, i32), b_cm: (i32, i32, i32), hops: usize) -> bool {
        if self.storeys.is_empty() {
            return true;
        }
        let (Some(sa), Some(sb)) = (self.storey_at_cm(a_cm.1), self.storey_at_cm(b_cm.1)) else {
            return true; // costura: no se decide, se envía
        };
        if sa != sb {
            // Verticalidad fuera de alcance a propósito: un hueco de escalera comunica plantas y
            // este grafo no lo sabe. Enviar de más es la dirección segura.
            return true;
        }
        self.storeys[sa].can_see((a_cm.0, a_cm.2), (b_cm.0, b_cm.2), hops)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::wg3::plan::{LinkKind, PlannedLink};

    /// Grafo a mano: cuatro salas en fila, 0—1—2 conectadas y 3 aislada.
    fn line_of_rooms() -> VisibilityGraph {
        VisibilityGraph {
            spaces_cm: vec![
                (0, 0, 1000, 1000),
                (1000, 0, 2000, 1000),
                (2000, 0, 3000, 1000),
                (5000, 0, 6000, 1000),
            ],
            neighbours: vec![vec![1], vec![0, 2], vec![1], vec![]],
        }
    }

    #[test]
    fn a_point_lands_in_its_room() {
        let g = line_of_rooms();
        assert_eq!(g.space_at_cm(500, 500), Some(0));
        assert_eq!(g.space_at_cm(1500, 500), Some(1));
        // Fuera de todo espacio: nadie, y el llamante lo trata como "visible para todos".
        assert_eq!(g.space_at_cm(4000, 500), None);
    }

    #[test]
    fn visibility_grows_with_hops() {
        let g = line_of_rooms();
        let mut one = g.visible_from(0, 1);
        one.sort_unstable();
        assert_eq!(one, vec![0, 1], "a un salto, la tuya y la vecina");

        let mut two = g.visible_from(0, 2);
        two.sort_unstable();
        assert_eq!(two, vec![0, 1, 2], "a dos, la de más allá");
    }

    /// La mitad que impide que esto haga desaparecer a nadie: ante la duda, se ve.
    #[test]
    fn unknown_positions_are_always_visible() {
        let g = line_of_rooms();
        assert!(
            g.can_see((4000, 500), (500, 500), 1),
            "un punto fuera de todo espacio no puede volver invisible a nadie"
        );
        assert!(g.can_see((500, 500), (4000, 500), 1));
    }

    /// Dos plantas iguales, la calle en el índice 0.
    fn two_storeys() -> RegionVisibility {
        RegionVisibility::new(vec![line_of_rooms(), line_of_rooms()], 0)
    }

    /// El corazón del blindaje: cerca de una costura NO se decide la planta, porque
    /// `storey_of_floor_cm` clasifica una abajo justo ahí y equivocarse vuelve a alguien invisible.
    #[test]
    fn near_a_storey_seam_nothing_is_decided() {
        let v = two_storeys();
        let h = crate::world::wg3::plan::STOREY_HEIGHT_CM;

        assert_eq!(
            v.storey_at_cm(h / 2),
            Some(0),
            "a media planta sí se decide"
        );
        assert_eq!(v.storey_at_cm(5), None, "justo sobre el forjado, duda");
        assert_eq!(v.storey_at_cm(h - 5), None, "justo bajo el techo, duda");
        assert_eq!(
            v.storey_at_cm(h + 5),
            None,
            "y en la costura de arriba también"
        );
    }

    /// Y lo que la duda provoca: se envía. Dos que estarían en salas incomunicadas se ven igual si
    /// uno de los dos anda por una costura.
    #[test]
    fn a_seam_forces_visibility() {
        let v = two_storeys();
        let h = crate::world::wg3::plan::STOREY_HEIGHT_CM;

        // Los dos a media planta y en salas incomunicadas (0 y 3): no se ven.
        assert!(!v.can_see(
            (500, h / 2, 500),
            (5500, h / 2, 500),
            DEFAULT_VISIBILITY_HOPS
        ));
        // El mismo par, pero uno en la costura: se ve, porque no se puede afirmar dónde está.
        assert!(v.can_see((500, 5, 500), (5500, h / 2, 500), DEFAULT_VISIBILITY_HOPS));
    }

    /// El PVS no opina de verticalidad: un hueco de escalera comunica plantas y este grafo no lo
    /// sabe, así que plantas distintas se envían siempre.
    #[test]
    fn different_storeys_always_see_each_other() {
        let v = two_storeys();
        let h = crate::world::wg3::plan::STOREY_HEIGHT_CM;
        assert!(v.can_see(
            (500, h / 2, 500),
            (5500, h + h / 2, 500),
            DEFAULT_VISIBILITY_HOPS
        ));
    }

    /// Una región sin grafo (todavía sin plan conservado) no puede ocultar a nadie.
    #[test]
    fn an_empty_region_sees_everything() {
        let v = RegionVisibility::default();
        assert!(v.can_see((0, 0, 0), (99_999, 0, 99_999), 0));
    }

    #[test]
    fn an_isolated_room_is_not_visible() {
        let g = line_of_rooms();
        // La sala 3 no tiene enlaces: desde la 0 no se ve ni con margen.
        assert!(!g.can_see((500, 500), (5500, 500), DEFAULT_VISIBILITY_HOPS));
        // Pero uno consigo mismo siempre.
        assert!(g.can_see((5500, 500), (5500, 500), 0));
    }

    /// El grafo sale del plan, y los enlaces son simétricos: el vano se cruza en los dos sentidos.
    #[test]
    fn from_plan_links_both_ways() {
        let plan = RegionPlan {
            spaces: Vec::new(),
            links: vec![PlannedLink {
                a: 0,
                b: 1,
                width_cm: 100,
                kind: LinkKind::Doorway,
                at_x_cm: 0,
                at_z_cm: 0,
            }],
            gates: Vec::new(),
            bounds_cm: None,
        };
        // Sin espacios, un enlace fuera de rango se ignora en vez de tumbar la generación: un plan
        // incoherente no puede reventar el mundo por culpa de una traza de diagnóstico.
        let g = VisibilityGraph::from_plan(&plan);
        assert!(g.is_empty());
        assert_eq!(g.visible_from(0, 2), Vec::<usize>::new());
    }
}
