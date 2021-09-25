using LA.Common;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ZeroLevel;
using ZeroLevel.Services.Serialization;
using ZeroLevel.WPF;
using Brushes = System.Drawing.Brushes;

namespace WavAnalizer
{
    public class Meta
    {
        public double min;
        public double max;
        public double freq_min;
        public double freq_max;
        public double am_min;
        public double am_max;
        public int rate;
        public TimeSpan time;
        public double[,] frames;
    }

    public enum PlayerState
    {
        None,
        Play,
        Paused
    }

    public class AudioFile
        : BaseViewModel
    {
        private const double width = 900;

        private PlayerState _state = PlayerState.None;
        private string _comment { get; set; }
        private WaveOutEvent player;
        private WaveStream waveStream;
        private VolumeSampleProvider volumeStream;

        public PlayerState PlayerState { get { return _state; } set { _state = value; OnPropertyChanged(nameof(PlayerState)); } }
        public string DisplayName => $"{FileName} {Comment}";
        public string FileName { get; private set; }
        public string FilePath { get; private set; }
        private string FileMetaPath;
        public ImageSource Spectr { get; set; }
        public string Comment { get { return _comment; } set { _comment = value; OnPropertyChanged(nameof(DisplayName)); } }

        public TimeSpan CurrentTime => waveStream?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan TotalTime => waveStream?.TotalTime ?? TimeSpan.Zero;

        #region Meta
        private readonly object _m_lock = new object();
        public Meta ReadMeta()
        {
            CheckMeta();
            return ReadMetaFile();
        }

        public void CheckMeta()
        {
            if (File.Exists(FileMetaPath) == false)
            {
                lock (_m_lock)
                {
                    if (File.Exists(FileMetaPath) == false)
                    {
                        CreateMeta();
                        Comment = "[Meta]";
                    }
                }
            }
        }

        private Meta ReadMetaFile()
        {
            var meta = new Meta();
            using (var reader = new MemoryStreamReader(new FileStream(FileMetaPath, FileMode.Open, FileAccess.Read, FileShare.None)))
            {
                meta.rate = reader.ReadInt32();
                meta.time = reader.ReadTimeSpan();
                meta.min = reader.ReadDouble();
                meta.max = reader.ReadDouble();
                meta.freq_min = reader.ReadDouble();
                meta.freq_max = reader.ReadDouble();
                meta.am_min = reader.ReadDouble();
                meta.am_max = reader.ReadDouble();
                var count = reader.ReadLong();
                meta.frames = new double[count, 6];
                for (long i = 0; i < count; i++)
                {
                    meta.frames[i, 0] = reader.ReadDouble();
                    meta.frames[i, 1] = reader.ReadDouble();
                    meta.frames[i, 2] = reader.ReadDouble();
                    meta.frames[i, 3] = reader.ReadDouble();
                    meta.frames[i, 4] = reader.ReadDouble();
                    meta.frames[i, 5] = reader.ReadDouble();
                }
            }
            return meta;
        }

        private void CreateMeta()
        {
            var (data, rate, time) = ReadWAV(FilePath);
            long frameWidth = (long)(data.Length / width);
            if (frameWidth < 1) frameWidth = 1L;
            double[,] frames = new double[(int)width, 6];
            float[] frame = new float[(int)frameWidth];

            double min = double.MaxValue, max = double.MinValue;
            double freq_min = double.MaxValue, freq_max = double.MinValue;
            double am_min = double.MaxValue, am_max = double.MinValue;

            for (long i = 0L; i < frames.GetLongLength(0); i++)
            {
                double frame_min = double.MaxValue, frame_max = double.MinValue;
                double sum_min = 0, sum_max = 0;
                for (long index = i * frameWidth; index < (i * frameWidth + frameWidth) && index < data.Length; index++)
                {
                    //var value = data[index] > 1 ? Math.Log10(data[index]) : data[index] < -1 ? -Math.Log2(-data[index]) : data[index];
                    //var value = data[index] > 0 ? Math.Sqrt(data[index]) : data[index] < 0 ? -Math.Sqrt(-data[index]) : data[index];
                    //var value = data[index];
                    var value = Math.Log10(1 + Math.Abs(data[index])) * (data[index] > 0 ? 10 : -10);

                    if (value > max) max = value;
                    if (value < min) min = value;

                    if (value > frame_max) frame_max = value;
                    if (value < frame_min) frame_min = value;

                    if (value > 0) sum_max += value;
                    else sum_min += value;

                    frame[index - i * frameWidth] = (float)data[index];
                }
                frames[i, 0] = frame_min;
                frames[i, 1] = frame_max;
                frames[i, 2] = sum_min / frameWidth;
                frames[i, 3] = sum_max / frameWidth;
                (frames[i, 4], frames[i, 5]) = FFTW.AverageFFT(frame, rate);

                if (frames[i, 4] < freq_min) freq_min = frames[i, 4];
                if (frames[i, 4] > freq_max) freq_max = frames[i, 4];

                if (frames[i, 5] < am_min) am_min = frames[i, 5];
                if (frames[i, 5] > am_max) am_max = frames[i, 5];
            }
            using (var writer = new MemoryStreamWriter(new FileStream(FileMetaPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.WriteInt32(rate);
                writer.WriteTimeSpan(time);
                writer.WriteDouble(min);
                writer.WriteDouble(max);
                writer.WriteDouble(freq_min);
                writer.WriteDouble(freq_max);
                writer.WriteDouble(am_min);
                writer.WriteDouble(am_max);
                writer.WriteLong(frames.GetLongLength(0));
                for (long i = 0; i < frames.GetLongLength(0); i++)
                {
                    writer.WriteDouble(frames[i, 0]);
                    writer.WriteDouble(frames[i, 1]);
                    writer.WriteDouble(frames[i, 2]);
                    writer.WriteDouble(frames[i, 3]);
                    writer.WriteDouble(frames[i, 4]);
                    writer.WriteDouble(frames[i, 5]);
                }
            }
        }

        public static (double[] audio, int sampleRate, TimeSpan time) ReadWAV(string filePath, double multiplier = 16_000)
        {
            using var afr = new AudioFileReader(filePath);
            int sampleRate = afr.WaveFormat.SampleRate;
            int bytesPerSample = afr.WaveFormat.BitsPerSample / 8;
            int sampleCount = (int)afr.Length / bytesPerSample;
            int channelCount = afr.WaveFormat.Channels;
            var audio = new List<double>(sampleCount);
            var buffer = new float[sampleRate * channelCount];
            int samplesRead = 0;
            while ((samplesRead = afr.Read(buffer, 0, buffer.Length)) > 0)
                audio.AddRange(buffer.Take(samplesRead).Select(x => x * multiplier));
            return (audio.ToArray(), sampleRate, afr.TotalTime);
        }
        #endregion

        public AudioFile(string path)
        {
            FileName = Path.GetFileNameWithoutExtension(path);
            FilePath = path;
            FileMetaPath = Path.ChangeExtension(path, ".meta");
            if (File.Exists(FileMetaPath))
            {
                Comment = "[Meta]";
            }
        }

        public void Remove()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            try
            {
                if (File.Exists(FileMetaPath))
                {
                    File.Delete(FileMetaPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void Play()
        {
            if (player == null)
            {
                player = new WaveOutEvent();
                waveStream = new AudioFileReader(FilePath);
                volumeStream = new VolumeSampleProvider(waveStream.ToSampleProvider());
                player.Init(volumeStream);
            }
            player.Play();
            PlayerState = PlayerState.Play;
        }

        public void Seek(int seconds)
        {
            if (player != null)
            {
                player.Pause();
                waveStream.CurrentTime = TimeSpan.FromSeconds(seconds);
                player.Play();
                PlayerState = PlayerState.Play;
            }
        }

        public void Pause()
        {
            if (player != null)
            {
                player.Pause();
                PlayerState = PlayerState.Paused;
            }
        }

        public void Resume()
        {
            if (player != null)
            {
                player.Play();
                PlayerState = PlayerState.Play;
            }
        }

        public void Volume(float volume)
        {
            if (player != null)
            {
                player.Volume = ((float)volume) / 100.0f;
            }
        }

        public void Destroy()
        {
            if (player != null)
            {
                player.Stop();
                player.Dispose();
                waveStream.Dispose();
                //volumeStream.Dispose();
            }
            PlayerState = PlayerState.None;
            player = null;
            waveStream = null;
            volumeStream = null;
        }
    }

    public class AudioContext
        : BaseViewModel
    {
        private string _currentText;
        private ImageSource _spectr;
        private ImageSource _ampitude;
        private AudioFile _selected;
        private int _currentTime;
        private int _totalTime;
        private int _prevCurrentTime = 0;
        private int _volume = 100;

        public AudioContext()
        {
            Files = new ObservableCollection<AudioFile>();
            Sheduller.RemindEvery(TimeSpan.FromSeconds(1), UpdateCurrentTime);
        }

        #region UI properties

        public string CurrentText { get { return _currentText; } set { _currentText = value; OnPropertyChanged(nameof(CurrentText)); } }
        public ObservableCollection<AudioFile> Files { get; set; }
        public AudioFile Selected { set { _selected = value; UpdateSpectrogram(); } }
        public ImageSource Spectr { get { return _spectr; } set { _spectr = value; OnPropertyChanged(nameof(Spectr)); } }
        public ImageSource Amplitude { get { return _ampitude; } set { _ampitude = value; OnPropertyChanged(nameof(Amplitude)); } }
        public int CurrentTime { get { return _currentTime;  } set { _currentTime = value; _selected?.Seek(_currentTime); OnPropertyChanged(nameof(CurrentTime)); } }
        public int TotalTime { get { return _totalTime; } set { _totalTime = value; OnPropertyChanged(nameof(TotalTime)); } }
        public int Volume { get { return _volume; } set { _volume = value; SetVolume(); OnPropertyChanged(nameof(Volume)); } }

        public ICommand LoadFilesCommand => new RelayCommand(_ => true, s => LoadFiles());
        public ICommand SpectrAnalizeFilesCommand => new RelayCommand(_ => true, s => SpectrAnalizeFiles());
        public ICommand OpenFileCommand => new RelayCommand(_ => true, s => OpenFile());
        public ICommand RemoveFileCommand => new RelayCommand(_ => true, s => RemoveFile());


        public ICommand PlayCommand => new RelayCommand(_ => true, s => Play());
        public ICommand PauseCommand => new RelayCommand(_ => true, s => Pause());
        public ICommand DestroyCommand => new RelayCommand(_ => true, s => Destroy());
        public ICommand PrevCommand => new RelayCommand(_ => true, s => Prev());
        public ICommand NextCommand => new RelayCommand(_ => true, s => Next());


        #endregion

        #region Playing
        private void UpdateCurrentTime()
        {
            var time = (int)(_selected?.CurrentTime.TotalSeconds ?? 0d);
            if (_prevCurrentTime != time)
            {
                _prevCurrentTime = time;
                _currentTime = time;
                Application.Current?.Dispatcher?.Invoke(()=> 
                {
                    OnPropertyChanged(nameof(CurrentTime));
                });
            }
        }

        private void SetVolume()
        {
            if (_selected != null)
            {
                try
                {
                    if (_selected.PlayerState == PlayerState.Play)
                    {
                        _selected.Volume(_volume);                        
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void Play()
        {
            if (_selected != null)
            {
                try
                {
                    if (_selected.PlayerState == PlayerState.None)
                    {
                        _selected.Play();
                        TotalTime = (int)_selected.TotalTime.TotalSeconds;
                        _prevCurrentTime = CurrentTime = 0;
                        Volume = 100;
                    }
                    else if (_selected.PlayerState == PlayerState.Paused)
                    {
                        _selected.Resume();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void Pause()
        {
            if (_selected != null)
            {
                try
                {
                    if (_selected.PlayerState == PlayerState.Play)
                    {
                        _selected.Pause();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void Destroy()
        {
            if (_selected != null)
            {
                try
                {
                    _selected.Destroy();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void Next()
        {
            if (_selected != null)
            {
                try
                {
                    _selected.Destroy();
                    /*
                     ToDo
                     */
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void Prev()
        {
            if (_selected != null)
            {
                try
                {
                    _selected.Destroy();
                    /*
                     ToDo
                     */
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }
        #endregion

        private void RemoveFile()
        {
            if (_selected != null)
            {
                try
                {
                    _selected.Destroy();
                    _selected.Remove();
                    Files.Remove(_selected);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void OpenFile()
        {
            if (_selected != null)
            {
                Process.Start(@"explorer", _selected.FilePath);
            }
        }

        private void SpectrAnalizeFiles()
        {
            Task.Factory.StartNew(() =>
            {
                foreach (var file in Files)
                {
                    file.CheckMeta();
                }
            });
        }

        private void LoadFiles()
        {
            VistaFolderBrowserDialog dialog = new VistaFolderBrowserDialog();
            dialog.Description = "Please select a folder.";
            dialog.UseDescriptionForTitle = true;
            if ((bool)dialog.ShowDialog())
            {
                Files.Clear();
                foreach (var file in Directory.GetFiles(dialog.SelectedPath, "*.wav"))
                {
                    Files.Add(new AudioFile(file));
                }
            }
        }

        private void UpdateSpectrogram()
        {
            if (_selected != null)
            {
                if (_selected.Spectr == null)
                {
                    try
                    {
                        var meta = _selected.ReadMeta();
                        using (var bmp = CreateVolumeImage(meta, 900, 250))
                        {
                            Spectr = BitmapSourceHelper.LoadBitmap(bmp);
                        }
                        using (var bmp = CreateAmplitudeImage(meta, 900, 250))
                        {
                            Amplitude = BitmapSourceHelper.LoadBitmap(bmp);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message);
                    }
                }
            }
        }

        private static Font font = new Font("Tahoma", 8);
        private static Bitmap CreateVolumeImage(Meta meta, double width, double height)
        {
            double KH = height / (meta.max - meta.min);
            var bmp = new Bitmap((int)width, (int)height);
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var time_size = g.MeasureString("00:00", font);
                    int time_period = (int)(time_size.Width * 2);
                    g.FillRectangle(Brushes.Black, 0, 0, (int)width, (int)height);
                    double fmin = (meta.frames[0, 0] - meta.min) * KH;
                    double fmax = (meta.frames[0, 1] - meta.min) * KH;
                    double fmmin_p, fmmax_p, fmmin_c, fmmax_c;
                    g.DrawLine(Pens.Red, 0, (int)fmin, 0, (int)fmax);
                    for (int i = 1; i < bmp.Width; i++)
                    {
                        fmin = (meta.frames[i, 0] - meta.min) * KH;
                        fmax = (meta.frames[i, 1] - meta.min) * KH;
                        fmmin_p = (meta.frames[i - 1, 2] - meta.min) * KH;
                        fmmax_p = (meta.frames[i - 1, 3] - meta.min) * KH;
                        fmmin_c = (meta.frames[i, 2] - meta.min) * KH;
                        fmmax_c = (meta.frames[i, 3] - meta.min) * KH;
                        g.DrawLine(Pens.Red, i, (int)fmin, i, (int)fmax);
                        g.DrawLine(Pens.Blue, i - 1, (int)fmmin_p, i, (int)fmmin_c);
                        g.DrawLine(Pens.Blue, i - 1, (int)fmmax_p, i, (int)fmmax_c);
                        if (i % time_period == 0)
                        {
                            var pos = (int)(meta.time.TotalSeconds * i / width);
                            g.DrawString(TimeToString(pos), font, Brushes.White, i, 10);
                        }
                    }
                }
            }
            return bmp;
        }

        private static Bitmap CreateAmplitudeImage(Meta meta, double width, double height)
        {
            double KA = height / (meta.am_max - meta.am_min);
            var bmp = new Bitmap((int)width, (int)height);
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var time_size = g.MeasureString("00:00", font);
                    int time_period = (int)(time_size.Width * 2);
                    g.FillRectangle(Brushes.Black, 0, 0, (int)width, (int)height);
                    for (int i = 1; i < bmp.Width; i++)
                    {
                        g.DrawLine(Pens.BlueViolet, i, (int)height, i, (int)(height - meta.frames[i, 5] * KA));
                        if (i % time_period == 0)
                        {
                            var pos = (int)(meta.time.TotalSeconds * i / width);
                            g.DrawString(TimeToString(pos), font, Brushes.White, i, 10);
                        }
                    }
                }
            }
            return bmp;
        }

        private static string TimeToString(int total)
        {
            var seconds = total % 60;
            total -= seconds;
            total /= 60;
            var minutes = total % 60;
            total -= minutes;
            if (total > 0)
            {
                return $"{total:D2}:{minutes:D2}:{seconds:D2}";
            }
            return $"{minutes:D2}:{seconds:D2}";
        }
    }
}
