using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
    public abstract class SurfaceIdentity<T> : SurfaceIdentity where T : Collider
    {
        public sealed override SurfaceDefinition GetSurfaceFromHit(in RaycastHit hit)
        {
            if (hit.collider is T col)
                return GetSurfaceFromHit(col, in hit);
                
            return null;
        }

        public sealed override SurfaceDefinition GetSurfaceFromCollision(Collision collision)
        {
            if (collision.collider is T col)
                return GetSurfaceFromCollision(col, collision);
                
            return null;
        }

        protected abstract SurfaceDefinition GetSurfaceFromHit(T col, in RaycastHit hit);
        protected abstract SurfaceDefinition GetSurfaceFromCollision(T col, Collision collision);

        protected virtual void Start()
        {
            if (!gameObject.HasComponent<T>())
                Debug.LogWarning($"No collider of type {typeof(T)} found on this game object.", gameObject);
        }
    }
    
    public abstract class SurfaceIdentity : MonoBehaviour
    {
        public abstract SurfaceDefinition GetSurfaceFromHit(in RaycastHit hit);
        public abstract SurfaceDefinition GetSurfaceFromCollision(Collision collision);
    }
}