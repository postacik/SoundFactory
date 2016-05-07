using System;
using System.ComponentModel;
using CSCore;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;

namespace SoundFactory
{
    public class MusicPlayer : Component
    {
        private ISoundOut mSoundOut;
        private IWaveSource mWaveSource;

		public event EventHandler<PlaybackStoppedEventArgs> PlaybackStopped;

        public PlaybackState PlaybackState
        {
            get
            {
                if (mSoundOut != null)
                    return mSoundOut.PlaybackState;
                return PlaybackState.Stopped;
            }
        }

        public TimeSpan Position
        {
            get
            {
                if (mWaveSource != null)
                    return mWaveSource.GetPosition();
                return TimeSpan.Zero;
            }
            set
            {
                if (mWaveSource != null)
                    mWaveSource.SetPosition(value);
            }
        }

        public TimeSpan Length
        {
            get
            {
                if (mWaveSource != null)
                    return mWaveSource.GetLength();
                return TimeSpan.Zero;
            }
        }

        public int Volume
        {
            get
            {
                if (mSoundOut != null)
                    return Math.Min(100, Math.Max((int)(mSoundOut.Volume * 100), 0));
                return 100;
            }
            set
            {
                if (mSoundOut != null)
                {
                    mSoundOut.Volume = Math.Min(1.0f, Math.Max(value / 100f, 0f));
                }
            }
        }

        public void Open(IWaveSource waveSource, MMDevice device)
        {
            CleanupPlayback();

            mWaveSource = waveSource;
            mSoundOut = new WasapiOut() {Latency = 100, Device = device};
            mSoundOut.Initialize(mWaveSource);
			if (PlaybackStopped != null) mSoundOut.Stopped += PlaybackStopped;
        }

        public void Play()
        {
            if (mSoundOut != null)
                mSoundOut.Play();
        }

        public void Pause()
        {
            if (mSoundOut != null)
                mSoundOut.Pause();
        }

        public void Stop()
        {
            if (mSoundOut != null)
                mSoundOut.Stop();
        }

        private void CleanupPlayback()
        {
            if (mSoundOut != null)
            {
                mSoundOut.Dispose();
                mSoundOut = null;
            }
            if (mWaveSource != null)
            {
                mWaveSource.Dispose();
                mWaveSource = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            CleanupPlayback();
        }
    }
}