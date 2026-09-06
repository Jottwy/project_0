//! **Resumen del lote de métricas de layout.**
//!
//! Lee el agregado que escribe [`super::layout_metrics`] (`batch.csv`, una fila por chunk) y saca
//! la distribución de cada métrica SOBRE LOS CHUNKS: media, mediana, desviación típica, p10 y p90.
//!
//! No mide nada: no toca el generador ni el módulo de métricas, sólo lee un CSV ya escrito. Existe
//! para tener una línea base contra la que comparar un cambio del generador.
//!
//! ```text
//! cargo test --release summarize_metrics_baseline -- --ignored --nocapture
//! ```

/// Las columnas de `batch.csv` que se resumen, con el nombre corto que sale en la tabla.
pub const TRACKED: [(&str, &str); 6] = [
    ("jaggedness", "jaggedness_mean"),
    ("occlusivity", "occlusivity_mean"),
    ("clustering", "clustering_mean"),
    ("drift", "drift_mean"),
    ("isovist_area", "isovist_area_mean"),
    ("tile_entropy", "tile_entropy"),
];

/// La distribución de UNA métrica sobre los chunks del lote.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Summary {
    pub n: usize,
    pub mean: f64,
    pub median: f64,
    pub stddev: f64,
    pub p10: f64,
    pub p90: f64,
}

impl Summary {
    /// `values` se ordena aquí: el llamante no necesita traerlo ordenado.
    pub fn of(values: &mut [f64]) -> Self {
        if values.is_empty() {
            return Self::default();
        }
        values.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        let n = values.len();
        let mean = values.iter().sum::<f64>() / n as f64;
        // Desviación típica POBLACIONAL: el lote es la población entera, no una muestra de ella.
        let var = values.iter().map(|v| (v - mean) * (v - mean)).sum::<f64>() / n as f64;
        Self {
            n,
            mean,
            median: percentile(values, 0.50),
            stddev: var.sqrt(),
            p10: percentile(values, 0.10),
            p90: percentile(values, 0.90),
        }
    }
}

/// Percentil por interpolación lineal sobre el vector YA ordenado.
fn percentile(sorted: &[f64], q: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    if sorted.len() == 1 {
        return sorted[0];
    }
    let pos = q * (sorted.len() - 1) as f64;
    let lo = pos.floor() as usize;
    let hi = pos.ceil() as usize;
    if lo == hi {
        return sorted[lo];
    }
    let t = pos - lo as f64;
    sorted[lo] * (1.0 - t) + sorted[hi] * t
}

/// Un CSV leído como cabecera + columnas de f64. Lo mínimo que hace falta: el agregado lo escribe
/// este mismo repositorio, así que no hay comillas, ni comas dentro de un campo, ni nada que
/// justifique un parser de verdad.
pub struct Table {
    pub header: Vec<String>,
    pub rows: Vec<Vec<f64>>,
}

impl Table {
    pub fn parse(text: &str) -> Option<Table> {
        let mut lines = text.lines().filter(|l| !l.trim().is_empty());
        let header: Vec<String> = lines
            .next()?
            .split(',')
            .map(|s| s.trim().to_string())
            .collect();
        let mut rows = Vec::new();
        for line in lines {
            let row: Vec<f64> = line
                .split(',')
                .map(|s| s.trim().parse::<f64>().unwrap_or(f64::NAN))
                .collect();
            if row.len() == header.len() {
                rows.push(row);
            }
        }
        Some(Table { header, rows })
    }

    /// Los valores de una columna por nombre, saltando los que no parsearon.
    pub fn column(&self, name: &str) -> Option<Vec<f64>> {
        let i = self.header.iter().position(|h| h == name)?;
        Some(
            self.rows
                .iter()
                .filter_map(|r| r.get(i).copied())
                .filter(|v| v.is_finite())
                .collect(),
        )
    }
}

/// Resume las [`TRACKED`] de un `batch.csv` ya leído.
pub fn summarize(batch_csv: &str) -> Vec<(&'static str, Summary)> {
    let Some(table) = Table::parse(batch_csv) else {
        return Vec::new();
    };
    TRACKED
        .iter()
        .filter_map(|(short, column)| {
            let mut v = table.column(column)?;
            Some((*short, Summary::of(&mut v)))
        })
        .collect()
}

pub const BASELINE_CSV_HEADER: &str = "metric,n,mean,median,stddev,p10,p90";

pub fn baseline_csv(rows: &[(&'static str, Summary)]) -> String {
    let mut s = String::from(BASELINE_CSV_HEADER);
    s.push('\n');
    for (name, u) in rows {
        s.push_str(&format!(
            "{},{},{:.6},{:.6},{:.6},{:.6},{:.6}\n",
            name, u.n, u.mean, u.median, u.stddev, u.p10, u.p90
        ));
    }
    s
}

/// La misma tabla, para stdout.
pub fn ascii_table(rows: &[(&'static str, Summary)]) -> String {
    let mut s = format!(
        "{:<14}{:>5}{:>12}{:>12}{:>12}{:>12}{:>12}\n",
        "metric", "n", "mean", "median", "stddev", "p10", "p90"
    );
    s.push_str(&"-".repeat(79));
    s.push('\n');
    for (name, u) in rows {
        s.push_str(&format!(
            "{:<14}{:>5}{:>12.4}{:>12.4}{:>12.4}{:>12.4}{:>12.4}\n",
            name, u.n, u.mean, u.median, u.stddev, u.p10, u.p90
        ));
    }
    s
}

#[cfg(test)]
mod runner {
    use super::*;
    use std::path::PathBuf;

    pub(super) fn metrics_dir() -> PathBuf {
        std::env::var("WG3_METRICS_OUT")
            .map(PathBuf::from)
            .unwrap_or_else(|_| {
                PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .join("viz_out")
                    .join("layout_metrics")
            })
    }

    #[test]
    fn percentiles_interpolate_and_bracket_the_data() {
        let mut v: Vec<f64> = (0..=10).map(|i| i as f64).collect();
        let u = Summary::of(&mut v);
        assert_eq!(u.n, 11);
        assert!((u.median - 5.0).abs() < 1e-9);
        assert!((u.p10 - 1.0).abs() < 1e-9);
        assert!((u.p90 - 9.0).abs() < 1e-9);
        assert!((u.mean - 5.0).abs() < 1e-9);
    }

    #[test]
    fn a_batch_csv_parses_into_the_tracked_columns() {
        let text = format!(
            "{}\n{}\n{}\n",
            super::super::layout_metrics::BATCH_CSV_HEADER,
            "1,1,1,2500,2000,0.8,0.5,300,1,100,1,0.4,0.01,80,1,0.7,0.01,8,1",
            "2,1,1,2500,1000,0.4,0.6,200,1,80,1,0.2,0.01,50,1,0.9,0.01,6,1"
        );
        let rows = summarize(&text);
        assert_eq!(rows.len(), TRACKED.len(), "falta alguna columna seguida");
        let jag = rows.iter().find(|(n, _)| *n == "jaggedness").unwrap().1;
        assert_eq!(jag.n, 2);
        assert!((jag.median - 65.0).abs() < 1e-9);
    }

    /// **LA LÍNEA BASE.** Lee el `batch.csv` del lote y escribe `baseline.csv` a su lado.
    #[test]
    #[ignore]
    fn summarize_metrics_baseline() {
        let dir = metrics_dir();
        let batch = dir.join("batch.csv");
        let text = std::fs::read_to_string(&batch)
            .unwrap_or_else(|e| panic!("no se pudo leer {}: {e}", batch.display()));
        let rows = summarize(&text);
        assert!(
            !rows.is_empty(),
            "el agregado no tiene ninguna métrica seguida"
        );
        let out = dir.join("baseline.csv");
        std::fs::write(&out, baseline_csv(&rows)).expect("no se pudo escribir la línea base");
        println!("{}", ascii_table(&rows));
        println!("[wg3-metrics] línea base → {}", out.display());
    }
}
