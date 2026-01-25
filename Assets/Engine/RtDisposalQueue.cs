using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class RtDisposalQueue
    {
        private readonly MonoBehaviour _host;
        private readonly List<RenderTexture> _rtToDestroy = new List<RenderTexture>();

        public RtDisposalQueue(MonoBehaviour host)
        {
            _host = host;
        }

        public void ScheduleDestroy(RenderTexture rt)
        {
            if (rt == null) return;
            try 
            { 
                rt.Release(); 
            }
            catch (Exception e) 
            { 
                Debug.LogWarning($"[RTQueue] Release error: {e.Message}"); 
            }

            lock (_rtToDestroy) 
            { 
                _rtToDestroy.Add(rt); 
            }
        }

        // Call from controller.LateUpdate()
        public void Drain()
        {
            lock (_rtToDestroy)
            {
                if (_rtToDestroy.Count == 0) return;

                for (int i = 0; i < _rtToDestroy.Count; i++)
                {
                    var r = _rtToDestroy[i];
                    if (r == null) continue;
                    try 
                    { 
                        UnityEngine.Object.Destroy(r); 
                    }
                    catch (Exception e) 
                    { 
                        Debug.LogWarning($"[RTQueue] Destroy error: {e.Message}"); 
                    }
                }
                _rtToDestroy.Clear();
            }
        }
    }
