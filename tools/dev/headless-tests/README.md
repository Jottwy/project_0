# Tests EditMode sin abrir Unity

Corre los ficheros de test **reales** de `Assets/Tests/EditMode` que no dependen de `UnityEngine`,
compilándolos con el SDK de .NET y un shim mínimo de NUnit.

```bash
dotnet build tools/dev/headless-tests/natverify.csproj -p:Repo=$(pwd) -v:q --nologo
dotnet tools/dev/headless-tests/bin/Debug/net8.0/natverify.dll
```

## Por qué existe

`Temp/UnityLockfile` sólo lo puede tener una sesión. Con el editor abierto —o con otra sesión
corriendo `-runTests`— la suite EditMode **no se puede lanzar**, y matar el proceso de otro no es
una opción. `CompileCheckClient.sh` contesta "compila", que no es lo mismo que "pasa": el
2026-08-31, `ParseEchoResponse` compilaba perfectamente y aceptaba `88.16.240` como IP pública,
porque `IPAddress.TryParse` admite la forma abreviada clásica. Eso salió **ejecutando**.

No sustituye a la suite. Es lo que se puede hacer cuando la suite está bloqueada.

## Qué cubre y qué no

- **Sí**: lógica pura y clases que sólo usan la BCL (`Assets/Scripts/Connectivity/`,
  `LobbyEndpointPolicy`), incluidos los tests que abren sockets en loopback.
- **No**: nada que toque `UnityEngine`, `Steamworks` o `MonoBehaviour`. Esos ficheros están
  excluidos del `.csproj` con un comentario que dice cuál y por qué.

Los ficheros a compilar se listan **a mano** en `natverify.csproj`, no con un glob: un glob
arrastraría el primer test que use Unity y el harness dejaría de compilar sin que quede claro por
qué. Añadir un fichero de test nuevo es añadir una línea.

## Lo que NO es

- No es un segundo motor de tests. El shim de NUnit implementa exactamente los asertos que usan
  estos ficheros; si un test nuevo usa otro, el harness no compila, y eso es lo correcto.
- No sustituye a `-runTests`. Antes de cerrar tarea, la suite EditMode se corre en Unity.
