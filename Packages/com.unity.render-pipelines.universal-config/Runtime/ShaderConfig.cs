//-----------------------------------------------------------------------------
// Configuration
//-----------------------------------------------------------------------------
using System;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Project-wide shader configuration options.
    /// </summary>
    /// <remarks>This enum will generate the proper shader defines.</remarks>
    ///<seealso cref="ShaderConfig"/>
    [GenerateHLSL]
    public static class ShaderOptions
    {
        /// <summary>Max number of lights supported on mobile with OpenGL 3.0 and below.</summary>
        public const int k_MaxVisibleLightCountLowEndMobile = 16;

        /// <summary>Max number of lights supported on mobile, OpenGL, and WebGPU platforms.</summary>
        public const int k_MaxVisibleLightCountMobile = 32;

        /// <summary>Max number of lights supported on desktop platforms.</summary>
        /// <remarks>
        /// 512 y no los 256 de serie. Este paquete está EMBEBIDO a propósito (era
        /// `Library/PackageCache`, que es caché de solo lectura y Unity regenera): es el único
        /// sitio donde se puede tocar el tope, porque la constante acaba compilada dentro de los
        /// shaders de URP.
        ///
        /// El motivo, medido el 08-09-2026 con el censo de `Wg3ChunkStreamer`: una planta de
        /// oficinas de WG3 pone entre 264 y 332 luces dentro del frustum. Por encima de 256, URP
        /// descarta las sobrantes SIN log ni warning — se apagan lámparas del fondo al girar la
        /// cámara y parece un bug del worldgen. Con 512 hay margen para subir la densidad de
        /// plafones, que es lo que pedía el aspecto Level 0.
        ///
        /// Lo que cuesta: el clustering de Forward+ reserva sitio para más luces por celda, así
        /// que sube la memoria del buffer de luces y el trabajo por frame. No es gratis; 512 es
        /// el doble justo, no un número redondo cualquiera.
        ///
        /// AL ACTUALIZAR URP: este paquete queda a nuestro cargo. Hay que comparar con la versión
        /// nueva de `Library/PackageCache` y volver a aplicar este cambio, que es solo esta línea
        /// y su gemela en `ShaderConfig.cs.hlsl` — los dos ficheros tienen que decir lo MISMO.
        /// </remarks>
        public const int k_MaxVisibleLightCountDesktop = 512;
    };
}
