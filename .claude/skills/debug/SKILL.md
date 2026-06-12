---
name: debug
description: Protocolo de depuración disciplinado con hipótesis y presupuesto de intentos, para evitar parches a ciegas y quema de tokens.
disable-model-invocation: true
---
Bug: $ARGUMENTS

Protocolo estricto:
1. Reproduce: comando/escenario exacto que falla. Si no es reproducible, instrumenta primero (logs mínimos) y para.
2. Formula máx. 3 hipótesis ordenadas por probabilidad, cada una con su evidencia.
3. Valida la hipótesis nº1 con la comprobación MÁS BARATA posible (lectura, log, test unitario) antes de tocar código.
4. Arregla solo la causa raíz. Prohibido: try/catch para silenciar, sleeps mágicos, refactor "ya que estoy".
5. Añade test de regresión o explica por qué no aplica.

Presupuesto: si tras 2 ciclos hipótesis→fix el bug sigue vivo, PARA y devuelve un informe de 10 líneas (síntomas, descartado, sospecha actual) para escalar a un modelo superior. No insistas a ciegas.
