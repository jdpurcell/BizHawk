using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Vortice.Multimedia;
using Vortice.XAudio2;

using BizHawk.Client.Common;

namespace BizHawk.Client.EmuHawk
{
	public class XAudio2SoundOutput : ISoundOutput
	{
		private bool _disposed;
		private Sound _sound;
		private IXAudio2 _device;
		private IXAudio2MasteringVoice _masteringVoice;
		private IXAudio2SourceVoice _sourceVoice;
		private BufferPool _bufferPool;
		private long _runningSamplesQueued;

		public XAudio2SoundOutput(Sound sound)
		{
			_sound = sound;
			_device = new IXAudio2();
			_masteringVoice = _device.CreateMasteringVoice(Sound.ChannelCount, Sound.SampleRate);
		}

		public void Dispose()
		{
			if (_disposed) return;

			_masteringVoice.Dispose();
			_masteringVoice = null;

			_device.Dispose();
			_device = null;

			_disposed = true;
		}

		public static IEnumerable<string> GetDeviceNames()
		{
			return Enumerable.Empty<string>();
		}

		private int BufferSizeSamples { get; set; }

		public int MaxSamplesDeficit { get; private set; }

		public void ApplyVolumeSettings(double volume)
		{
			_sourceVoice.Volume = (float)volume;
		}

		public void StartSound()
		{
			BufferSizeSamples = Sound.MillisecondsToSamples(Global.Config.SoundBufferSizeMs);
			MaxSamplesDeficit = BufferSizeSamples;

			_sourceVoice = _device.CreateSourceVoice(new WaveFormat(Sound.SampleRate, Sound.ChannelCount));

			_bufferPool = new BufferPool();
			_runningSamplesQueued = 0;

			_sourceVoice.Start();
		}

		public void StopSound()
		{
			_sourceVoice.Stop();
			_sourceVoice.Dispose();
			_sourceVoice = null;

			_bufferPool.Dispose();
			_bufferPool = null;

			BufferSizeSamples = 0;
		}

		public int CalculateSamplesNeeded()
		{
			bool isInitializing = _runningSamplesQueued == 0;
			bool detectedUnderrun = !isInitializing && _sourceVoice.State.BuffersQueued == 0;
			long samplesAwaitingPlayback = _runningSamplesQueued - (long)_sourceVoice.State.SamplesPlayed;
			int samplesNeeded = (int)Math.Max(BufferSizeSamples - samplesAwaitingPlayback, 0);
			if (isInitializing || detectedUnderrun)
			{
				_sound.HandleInitializationOrUnderrun(detectedUnderrun, ref samplesNeeded);
			}
			return samplesNeeded;
		}

		public void WriteSamples(short[] samples, int sampleOffset, int sampleCount)
		{
			if (sampleCount == 0) return;
			_bufferPool.Release(_sourceVoice.State.BuffersQueued);
			int byteCount = sampleCount * Sound.BlockAlign;
			AudioBuffer buffer = _bufferPool.Obtain(byteCount).Buffer;
			Marshal.Copy(samples, sampleOffset * Sound.ChannelCount, buffer.AudioDataPointer, sampleCount * Sound.ChannelCount);
			buffer.AudioBytes = byteCount;
			_sourceVoice.SubmitSourceBuffer(buffer);
			_runningSamplesQueued += sampleCount;
		}

		private class BufferPool : IDisposable
		{
			private readonly List<BufferPoolItem> _availableItems = new List<BufferPoolItem>();
			private readonly Queue<BufferPoolItem> _obtainedItems = new Queue<BufferPoolItem>();

			public void Dispose()
			{
				_availableItems.Clear();
				_obtainedItems.Clear();
			}

			public BufferPoolItem Obtain(int length)
			{
				BufferPoolItem item = GetAvailableItem(length) ?? new BufferPoolItem(length);
				_obtainedItems.Enqueue(item);
				return item;
			}

			private BufferPoolItem GetAvailableItem(int length)
			{
				int foundIndex = -1;
				for (int i = 0; i < _availableItems.Count; i++)
				{
					if (_availableItems[i].MaxLength >= length && (foundIndex == -1 || _availableItems[i].MaxLength < _availableItems[foundIndex].MaxLength))
						foundIndex = i;
				}
				if (foundIndex == -1) return null;
				BufferPoolItem item = _availableItems[foundIndex];
				_availableItems.RemoveAt(foundIndex);
				return item;
			}

			public void Release(int buffersQueued)
			{
				while (_obtainedItems.Count > buffersQueued)
					_availableItems.Add(_obtainedItems.Dequeue());
			}

			public class BufferPoolItem
			{
				public int MaxLength { get; }
				public AudioBuffer Buffer { get; }

				public BufferPoolItem(int length)
				{
					MaxLength = length;
					Buffer = new AudioBuffer(length, BufferFlags.None);
				}
			}
		}
	}
}
