using System.Collections.Generic;
using UnityEngine;

namespace MusicGame.Core
{
    /// <summary>
    /// 通用 GameObject 对象池：Rent 借出（已激活），Return 归还（失活复用）；
    /// 池空时自动扩容新建。所有实例挂在池对象下，销毁时统一清理。
    /// </summary>
    public class NotePool : MonoBehaviour
    {
        private GameObject _prefab;                                              // 池化预制体
        private readonly Stack<GameObject> _freeStack = new Stack<GameObject>(); // 空闲栈（LIFO）
        private readonly HashSet<GameObject> _freeSet = new HashSet<GameObject>();   // 空闲集合，防重复归还
        private readonly HashSet<GameObject> _ownedSet = new HashSet<GameObject>();  // 本池全部实例，拒绝外来对象
        private readonly List<GameObject> _ownedList = new List<GameObject>();       // 顺序引用，便于销毁

        /// <summary>初始化池：指定预制体并预热 initialCount 个空闲实例。</summary>
        public void Setup(GameObject notePrefab, int initialCount)
        {
            _prefab = notePrefab;
            ClearAll();
            for (int i = 0; i < Mathf.Max(0, initialCount); i++)
            {
                GameObject go = CreateInstance();
                _freeStack.Push(go);
                _freeSet.Add(go);
            }
        }

        /// <summary>借出一个对象（已激活）；空闲不足时自动扩容新建。</summary>
        public GameObject Rent()
        {
            while (_freeStack.Count > 0)
            {
                GameObject go = _freeStack.Pop();
                _freeSet.Remove(go);
                if (go == null) continue; // 已被外部销毁，跳过
                go.SetActive(true);
                return go;
            }
            GameObject created = CreateInstance(); // 自动扩容
            created.SetActive(true);
            return created;
        }

        /// <summary>归还对象：失活并回收入池。空引用 / 重复归还 / 外来对象一律忽略。</summary>
        public void Return(GameObject note)
        {
            if (note == null) return;
            if (!_ownedSet.Contains(note) || _freeSet.Contains(note)) return;
            note.SetActive(false);
            _freeStack.Push(note);
            _freeSet.Add(note);
        }

        private GameObject CreateInstance()
        {
            GameObject go = _prefab != null
                ? Instantiate(_prefab, transform)
                : new GameObject("PooledObject"); // 无预制体兜底（正常流程不应发生）
            go.transform.SetParent(transform, false);
            go.SetActive(false);
            _ownedSet.Add(go);
            _ownedList.Add(go);
            return go;
        }

        private void ClearAll()
        {
            for (int i = 0; i < _ownedList.Count; i++)
            {
                if (_ownedList[i] != null) Destroy(_ownedList[i]);
            }
            _ownedList.Clear();
            _ownedSet.Clear();
            _freeStack.Clear();
            _freeSet.Clear();
        }

        private void OnDestroy()
        {
            ClearAll();
        }
    }
}
