#ifndef BR_GARMENT_DAMAGE_INCLUDED
#define BR_GARMENT_DAMAGE_INCLUDED

// ADR-149 R4b: rotura de la ropa por zona del cuerpo, con la forma de lo que la rompió.
// Cada vértice lleva, horneado en el editor: uv3.x = su zona (por el hueso que más lo mueve) y uv4 = sus coordenadas EN
// METROS alrededor del eje de esa zona (u: vuelta, con 0 al frente; v: a lo largo). Así un agujero de bala mide lo que
// mide y no resbala al animar. El estado llega por MaterialPropertyBlock: 4 zonas por vector (zona 0 en A.x), y cada
// código es daño + 8 * corte (GarmentDamage + 8 * GarmentCut). Fuera de UnityPerMaterial a propósito.
float4 _GarmentZonesA;
float4 _GarmentZonesB;
float4 _GarmentZonesC;
float4 _GarmentZonesD;

half4 _GarmentLiningColor;
half4 _GarmentTapeColor;
half4 _GarmentThreadColor;
float _GarmentInflate; // metros hacia fuera, por la normal: la prenda de encima no pisa la de debajo
float _GarmentViewBias; // metros hacia la cámara al dibujar: gana la de encima donde se cruza con la de dentro

struct GarmentDamage
{
    float clipMask; // 1: agujero, no se pinta
    float edge;     // borde deshilachado o quemado
    float burn;     // 1 si el borde es de bala (más oscuro)
    float tape;     // parche de cinta
    float stitch;   // hilo de la costura
    float scar;     // la tela cosida queda algo más oscura
};

float GarmentZoneCode(float zone)
{
    int z = (int)(zone + 0.5);
    float4 v = z < 4 ? _GarmentZonesA : (z < 8 ? _GarmentZonesB : (z < 12 ? _GarmentZonesC : _GarmentZonesD));
    return v[z % 4];
}

float GarmentHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float2 GarmentRotate(float2 p, float angle)
{
    float c = cos(angle);
    float s = sin(angle);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

float GarmentSegment(float2 p, float2 a, float2 b, float r)
{
    float2 pa = p - a;
    float2 ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h) - r;
}

// Distancia con signo (metros) a la forma del daño, en el marco girado de la forma. halfSize: medio tamaño del parche de
// cinta en ese marco. Cortes (GarmentCut): 1 bala, 2 puñalada, 3 tajo; el desgarro es siempre un zarpazo.
float GarmentShape(float2 q, int cut, bool torn, float zone, out float2 p, out float2 halfSize)
{
    float jag = GarmentHash(floor(q * 900.0) + zone * 3.7) - 0.5; // ~1 mm de irregularidad
    if (torn)
    {
        p = GarmentRotate(q, 0.35);
        halfSize = float2(0.075, 0.045);
        float r = 0.0055 + 0.0015 * jag;
        float d = GarmentSegment(p, float2(-0.055, -0.024), float2(0.05, -0.02), r);
        d = min(d, GarmentSegment(p, float2(-0.06, 0.0), float2(0.058, 0.004), r * 1.2));
        d = min(d, GarmentSegment(p, float2(-0.05, 0.024), float2(0.052, 0.026), r));
        return d;
    }
    if (cut == 1)
    {
        p = q;
        halfSize = float2(0.022, 0.022);
        return length(q) - (0.007 + 0.0008 * jag);
    }
    if (cut == 2)
    {
        p = GarmentRotate(q, 1.05);
        halfSize = float2(0.028, 0.014);
        return GarmentSegment(p, float2(-0.016, 0.0), float2(0.016, 0.0), 0.003 + 0.0006 * jag);
    }
    p = GarmentRotate(q, 0.55);
    halfSize = float2(0.066, 0.02);
    return GarmentSegment(p, float2(-0.05, 0.0), float2(0.05, 0.0), 0.0038 + 0.0008 * jag);
}

// front: coseno de la vuelta alrededor del eje (1 al frente, -1 detrás). Los triángulos que mezclan dos zonas interpolan
// entre marcos distintos, y los de atrás cruzan el salto de la vuelta: ahí no se pinta daño.
GarmentDamage EvaluateGarmentDamage(float zone, float front, float2 coords)
{
    GarmentDamage d = (GarmentDamage)0;
    if (abs(zone - round(zone)) > 0.02 || front < 0.0)
        return d;
    float code = GarmentZoneCode(zone);
    if (code < 0.5)
        return d;

    int cut = (int)(code / 8.0 + 0.001);
    int damage = (int)(code - cut * 8.0 + 0.5);
    if (damage == 0)
        return d;
    if (cut == 0)
        cut = 3; // estado anterior al tipo de corte: tajo

    // Sitio fijo en la zona: al frente y a media altura, algo desplazado por zona para que izquierda y derecha no calquen.
    float2 offset = (float2(GarmentHash(float2(zone, 1.3)), GarmentHash(float2(zone, 7.9))) - 0.5) * 0.03;
    float2 p;
    float2 halfSize;
    float sdf = GarmentShape(coords - offset, cut, damage == 2, zone, p, halfSize);

    if (damage <= 2)
    {
        d.clipMask = sdf < 0.0 ? 1.0 : 0.0;
        float ring = cut == 1 && damage == 1 ? 0.0045 : 0.003;
        d.edge = saturate(1.0 - sdf / ring) * (sdf >= 0.0 ? 1.0 : 0.0);
        d.burn = cut == 1 && damage == 1 ? 1.0 : 0.0;
    }
    else if (damage == 3)
    {
        float2 t = abs(p) / halfSize;
        float box = max(t.x, t.y);
        d.tape = box < 1.0 ? 1.0 : 0.0;
        d.edge = box < 1.0 && box > 0.93 ? 0.4 : 0.0;
    }
    else
    {
        float seam = abs(sdf) < 0.0008 ? 1.0 : 0.0;
        float ticks = (sdf < 0.0035 && frac((p.x + 0.5 * p.y) / 0.004) < 0.28) ? 1.0 : 0.0;
        d.stitch = saturate(seam + ticks);
        d.scar = sdf < 0.002 ? 1.0 : 0.0;
    }
    return d;
}

#endif
