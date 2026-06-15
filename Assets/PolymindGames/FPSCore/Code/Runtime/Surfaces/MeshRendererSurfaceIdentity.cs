using System;
using UnityEngine;

namespace PolymindGames.SurfaceSystem
{
	[AddComponentMenu("Polymind Games/Surfaces/Mesh Surface Identity")]
    public sealed class MeshRendererSurfaceIdentity : SurfaceIdentity<MeshCollider>
    {
	    [SerializeField, ReorderableList(ListStyle.Lined, "Material", true)]
	    [DataReference(NullElement = "", HasAssetReference = true)]
	    [Help("Each surface corresponds with the material on the same index.")]
	    private DataIdReference<SurfaceDefinition>[] _materialSurfaceData = Array.Empty<DataIdReference<SurfaceDefinition>>();
        
        protected override SurfaceDefinition GetSurfaceFromHit(MeshCollider col, in RaycastHit hit)
        {
	        var mesh = col.sharedMesh;
	        
	        bool isConvexOrUnreadable = col.convex || !mesh.isReadable;
	        if (isConvexOrUnreadable)
		        return _materialSurfaceData[0].Def;
	        
	        int materialIndex = GetSubMeshIndex(mesh, hit.triangleIndex);
	        return _materialSurfaceData[materialIndex].Def;
        }
        
        protected override SurfaceDefinition GetSurfaceFromCollision(MeshCollider col, Collision collision)
        {
	        var contact = collision.contacts[0];
	        Ray ray = new Ray(contact.point + contact.normal * 0.05f, -contact.normal);
	        return contact.otherCollider.Raycast(ray, out var hit, 0.1f)
		        ? GetSurfaceFromHit(col, in hit)
		        : null;
        }
        
        private static int GetSubMeshIndex(Mesh mesh, int triangleIndex)
        {
	        int materialIndex = -1;
	        int lookupIndex1 = mesh.triangles[triangleIndex * 3];
	        int lookupIndex2 = mesh.triangles[triangleIndex * 3 + 1];
	        int lookupIndex3 = mesh.triangles[triangleIndex * 3 + 2];
        
	        for (int i = 0;i < mesh.subMeshCount;i++)
	        {
		        int[] triangles = mesh.GetTriangles(i);
        
		        for (int j = 0; j < triangles.Length; j += 3)
		        {
			        if (triangles[j] == lookupIndex1 && triangles[j + 1] == lookupIndex2 && triangles[j + 2] == lookupIndex3)
			        {
				        materialIndex = i;
				        break;
			        }
		        }
        
		        if (materialIndex != -1)
			        break;
	        }

	        return materialIndex;
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset() => OnValidate();
        
        private void OnValidate()
        {
            if (TryGetComponent(out MeshRenderer meshRend))
            {
	            var subMeshCount = meshRend.sharedMaterials.Length;
	            if (_materialSurfaceData.Length != subMeshCount)
	            {
		            var newArray = new DataIdReference<SurfaceDefinition>[subMeshCount];
		            int copyLength = newArray.Length < _materialSurfaceData.Length
			            ? newArray.Length
			            : _materialSurfaceData.Length;

		            Array.Copy(_materialSurfaceData, newArray, copyLength);
		            _materialSurfaceData = newArray;

		            UnityEditor.EditorUtility.SetDirty(this);
	            }
            }
        }
#endif
        #endregion
    }
}
