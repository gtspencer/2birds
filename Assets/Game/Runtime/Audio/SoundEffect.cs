using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Sound Effect")]
    public sealed class SoundEffect : ScriptableObject
    {
        [SerializeField] private AudioClip[] clips;
        [SerializeField, Range(0f, 1f)] private float volume = 1f;
        [SerializeField] private float pitchMin = 1f;
        [SerializeField] private float pitchMax = 1f;
        [SerializeField, Range(0f, 1f)] private float spatialBlend = 1f;

        public AudioClip Clip => clips.Length > 0 ? clips[Random.Range(0, clips.Length)] : null;
        public float Volume => volume;
        public float Pitch => Random.Range(pitchMin, pitchMax);
        public float SpatialBlend => spatialBlend;
    }
}
