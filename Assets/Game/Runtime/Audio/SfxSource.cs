using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class SfxSource : MonoBehaviour
    {
        private AudioSource source;

        private void Awake()
        {
            source = GetComponent<AudioSource>();
            source.playOnAwake = false;
        }

        public void Play(SoundEffect sfx)
        {
            if (sfx == null) return;
            var clip = sfx.Clip;
            if (clip == null) return;
            source.spatialBlend = sfx.SpatialBlend;
            source.pitch = sfx.Pitch;
            source.PlayOneShot(clip, sfx.Volume);
        }

        public void SetLoop(SoundEffect sfx)
        {
            if (sfx == null) return;
            var clip = sfx.Clip;
            if (clip == null) return;
            source.spatialBlend = sfx.SpatialBlend;
            source.pitch = sfx.Pitch;
            source.volume = sfx.Volume;
            source.clip = clip;
            source.loop = true;
            source.Play();
        }

        public void StopLoop()
        {
            source.loop = false;
            source.Stop();
            source.clip = null;
        }
    }
}
