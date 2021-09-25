using NAudio.Dsp;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WavAnalizer
{
    public static class FFTW
    {
        static void ComputeFrequencies(int sampleRate, int sizeOfFftWindow,
                                       out List<float> frequencies)
        {
            float currentFreq = 0;
            float sizeOfWindow = (float)Math.Pow(2, sizeOfFftWindow);
            float deltaV = sampleRate / sizeOfWindow;

            frequencies = new List<float>((int)sizeOfWindow);

            for (int i = 0; i < sizeOfWindow / 2; i++)
            {
                frequencies.Add(currentFreq);
                currentFreq += deltaV;
            }
        }

        static IEnumerable<(int WindowCounter, float Freq, double Amplitude)> GetFftData(float[] samples, int sampleRate, int windowSize = 10)
        {
            List<float> frequencies;
            ComputeFrequencies(sampleRate, windowSize, out frequencies);

            var size = (int)Math.Pow(2, windowSize);

            var windowCounter = 0;
            foreach (var window in samples.ReadAsWindows(windowSize))
            {
                windowCounter++;
                var data = window.Select(x => new Complex { X = x }).ToList();

                if (data.Count < size)
                {
                    // последнее окно, достраиваем до размера
                    data.AddRange(new Complex[size - data.Count]);
                }

                FastFourierTransform.FFT(true, windowSize, data.ToArray());

                var index = 0;
                foreach (var res in data)
                {
                    if (index == frequencies.Count)
                        break;

                    var ampl = Math.Sqrt(res.X * res.X + res.Y * res.Y);
                    yield return (windowCounter, frequencies[index++], ampl);
                }
            }
        }

        public static (double Freq, double Amplitude) AverageFFT(float[] frames, int sampleRate, int windowSize = 10)
        {
            float f = 0;
            double a = 0;
            double counter = 0;
            foreach (var (w, fr, am) in GetFftData(frames, sampleRate, windowSize))
            {
                f += fr;
                a += am;
                counter += 1;
            }
            return (f / counter, a / counter);
        }
    }

    public static class Extensions
    {
        public static IEnumerable<IEnumerable<float>> ReadAsWindows(this IEnumerable<float> samples, int windowSize = 10)
        {
            var count = samples.Count();

            var size = (int)Math.Pow(2, windowSize);

            if (count == 0)
                yield break;

            var windowsCount = (int)Math.Ceiling((double)count / size);

            for (int i = 0; i < windowsCount; i++)
                yield return samples.Skip(i * size).Take(size);
        }
    }
}
