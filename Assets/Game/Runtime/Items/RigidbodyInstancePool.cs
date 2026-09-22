using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    internal sealed class RigidbodyInstancePool<T> where T : Component
    {
        private readonly Stack<T> spare = new();
        internal T Rent(GameObject prefab, Scene scene)
        {
            if (spare.Count > 0) return spare.Pop();
            var item = Object.Instantiate(prefab).GetComponent<T>();
            SceneManager.MoveGameObjectToScene(item.gameObject, scene);
            return item;
        }
        internal void Return(T item) { item.gameObject.SetActive(false); spare.Push(item); }
        internal void Clear()
        {
            foreach (var item in spare) if (item) Object.Destroy(item.gameObject);
            spare.Clear();
        }
    }
}
