using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.DSP;
using BuzzGUI.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SpectrumBlock
{

    public partial class SpectrumCanvas : Canvas, INotifyPropertyChanged
    {
        public SpectrumBlockMachine SpectrumBlockMachine { get; }
        public IPattern Pat { get; }
        public IMachine Machine { get; }
        public double TickHeight { get; internal set; }
        private float [] AudioBuffer { get; set; }
        
        public IMachine TargetMaster { get; private set; }
        public IMachineConnection TargetConnection { get; private set; }

        private int audioBufferFillPosition = 0;
        
        private int prevTick = -1;
        private readonly int FFT_SIZE = 2048;

        private double[] lowTable;
        private double[] midTable;
        private double[] highTable;
        private float[] volumeTable;

        private readonly int AUDIO_BUFFER_SIZE = 10 * 2048;

        float maxSampleL;
        float maxSampleR;
        public double VUMeterLevelL { get; set; }
        public double VUMeterLevelR { get; set; }

        const double VUMeterRange = 80.0;

        DispatcherTimer graphTimer = new DispatcherTimer();
        int graphPos;

        public SpectrumCanvas(SpectrumBlockMachine sbm, IPattern pat, double tickHeight, double width, double height)
        {
            InitializeComponent();
            this.SpectrumBlockMachine = sbm;
            this.Pat = pat;
            Machine = SpectrumBlockMachine.host.Machine;

            ResourceDictionary rd = GetBuzzThemeResources();
            if (rd != null)
                this.Resources.MergedDictionaries.Add(rd);

            graphTimer.Tick += GraphTimer_Tick;

            this.Width = width;
            this.Height = height;
            TickHeight = tickHeight;

            AudioBuffer = new float[AUDIO_BUFFER_SIZE];

            this.SnapsToDevicePixels = true;
            ClipToBounds = true;
            this.Clip = new RectangleGeometry(new Rect(0, 1, Width, Height - 2));

            LinearGradientBrush bgBrush = TryFindResource("SpectrumBackgroundBrush") as LinearGradientBrush;
            Background = bgBrush;

            ContextMenu cmCanvas = new ContextMenu() { Margin = new Thickness(4, 4, 4, 4) };
            this.ContextMenu = cmCanvas;
            ContextMenu.ContextMenuOpening += ContextMenu_ContextMenuOpening;

            Init();

            if (sbm.MachineState.Source == "Master")
            {
                var macTarget = Global.Buzz.Song.Machines.SingleOrDefault(m => m.Name == "Master");
                Global.Buzz.MasterTap += Machine_Tap;
                TargetMaster = macTarget;
                this.ToolTip = "Master";
            }
            else
            {
                var mac = Global.Buzz.Song.Machines.Where(m => m.Name == SpectrumBlockMachine.MachineState.Source).FirstOrDefault();
                if (mac != null)
                {
                    var conn = mac.Outputs.Where(c => c.Destination.Name == SpectrumBlockMachine.MachineState.Destination).FirstOrDefault();
                    if (conn != null)
                    {
                        TargetConnection = conn;
                        TargetConnection.Tap += Machine_Tap;
                        this.ToolTip = TargetConnection.Source.Name + " -> " + TargetConnection.Destination.Name;
                    }
                }
            }
            
            this.MouseMove += (sender, e) =>
            {
                Point mousePos = e.GetPosition(this);
                int yIndex = (int)(mousePos.Y / TickHeight);
                if (yIndex >= 0 && yIndex < volumeTable.Length)
                {
                    polygonVol.ToolTip = "Volume: " + (volumeTable[yIndex] * VUMeterRange - VUMeterRange).ToString("0.0") + " dB";

                    polygonLow.ToolTip = "Low Range: 0Hz - " + SpectrumBlockMachine.Low + "Hz | " + (lowTable[yIndex] * VUMeterRange - VUMeterRange).ToString("0.0") + " dB";
                    polygonMid.ToolTip = "Mid Range: " + SpectrumBlockMachine.Low + "Hz - " + SpectrumBlockMachine.High + "Hz | " + (midTable[yIndex] * VUMeterRange - VUMeterRange).ToString("0.0") + " dB";
                    polygonHigh.ToolTip = "High Range: " + SpectrumBlockMachine.High + "Hz - 22050Hz | " + (highTable[yIndex] * VUMeterRange - VUMeterRange).ToString("0.0") + " dB";
                    double bal = 2 * ((polylineBalance.Points[yIndex].X - 0.1 * Width) / Width / 0.8 - 0.5);
                    string txt = "";
                    if (bal < 0)
                        txt = " L";
                    else if (bal == 0)
                        txt = " C";
                    else
                        txt = " R";
                    polylineBalance.ToolTip = bal.ToString("0.0%") + txt;

                    double corrCoe = 2 * ((polylineCorrelationCoefficient.Points[yIndex].X - 0.1 * Width) / Width / 0.8) - 1;
                    polylineCorrelationCoefficient.ToolTip = "CorrCoe: " + corrCoe.ToString("0.0");
                }
            };

            Machine.GetParameter("Scale").SubscribeEvents(0, ScaleChanged, null);
            Machine.GetParameter("LowVisible").SubscribeEvents(0, LowVisibleChanged, null);
            Machine.GetParameter("MidVisible").SubscribeEvents(0, MidVisibleChanged, null);
            Machine.GetParameter("HighVisible").SubscribeEvents(0, HighVisibleChanged, null);
            Machine.GetParameter("VolumeVisible").SubscribeEvents(0, VolumeVisibleChanged, null);
            Machine.GetParameter("BalanceVisible").SubscribeEvents(0, BalanceVisibleChanged, null);
            Machine.GetParameter("CorrCoeVisible").SubscribeEvents(0, CorrCoeVisibleChanged, null);

            polygonLow.Opacity = SpectrumBlockMachine.LowVisible ? 1 : 0;
            polygonMid.Opacity = SpectrumBlockMachine.MidVisible ? 1 : 0;
            polygonHigh.Opacity = SpectrumBlockMachine.HighVisible ? 1 : 0;
            polygonVol.Opacity = SpectrumBlockMachine.VolumeVisible ? 1 : 0;
            polylineBalance.Opacity = SpectrumBlockMachine.BalanceVisible ? 1 : 0;
            polylineCorrelationCoefficient.Opacity = SpectrumBlockMachine.CorrCoeVisible ? 1 : 0;

            SpectrumBlockMachine.PropertyChanged += SpectrumBlockMachine_PropertyChanged;
            Unloaded += SpectrumCanvas_Unloaded;
        }

        private ResourceDictionary GetBuzzThemeResources()
        {
            ResourceDictionary skin = new ResourceDictionary();

            try
            {
                string selectedTheme = Global.Buzz.SelectedTheme == "<default>" ? "Default" : Global.Buzz.SelectedTheme;
                string skinPath = Global.BuzzPath + "\\Themes\\" + selectedTheme + "\\Gear\\SpectrumBlock\\SpectrumCanvas.xaml";

                skin.Source = new Uri(skinPath, UriKind.Absolute);
            }
            catch (Exception)
            {
            }

            return skin;
        }

        private void SpectrumBlockMachine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Closing")
            {
                DisableEvents();
            }
        }

        private void CorrCoeVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polylineCorrelationCoefficient, param.GetValue(track) == 1);
        }

        private void BalanceVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polylineBalance, param.GetValue(track) == 1);
        }

        private void LowVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polygonLow, param.GetValue(track) == 1);
        }

        private void MidVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polygonMid, param.GetValue(track) == 1);
        }

        private void HighVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polygonHigh, param.GetValue(track) == 1);
        }

        private void VolumeVisibleChanged(IParameter param, int track)
        {
            SetAnimation(polygonVol, param.GetValue(track) == 1);
        }

        void ScaleChanged(IParameter param, int track)
        {
            graphTimer.Stop();
            graphTimer.Interval = TimeSpan.FromMilliseconds(1);
            graphPos = 0;
            graphTimer.Start();
        }

        // Update polygon data in small chunks. Keeps UI responsive.
        private void GraphTimer_Tick(object sender, EventArgs e)
        {
            int end = graphPos + 100;
            for (int i = graphPos; i < end; i++)
            {
                if (i >= lowTable.Length)
                {
                    graphTimer.Stop();
                    break;
                }
                UpdatePolygon(i);
                UpdateVol(i);
                graphPos++;
            }
        }

        private void SetAnimation(FrameworkElement polygon, bool isVisible)
        {
            var myDoubleAnimation = new DoubleAnimation();
            if (isVisible)
            {
                myDoubleAnimation.From = 0.0;
                myDoubleAnimation.To = 1.0;
                polygon.IsHitTestVisible = true;
            }
            else
            {
                myDoubleAnimation.From = 1.0;
                myDoubleAnimation.To = 0.0;
                polygon.IsHitTestVisible = false;
            }
            myDoubleAnimation.Duration = new Duration(TimeSpan.FromSeconds(0.2));
            myDoubleAnimation.AutoReverse = false;

            Storyboard myStoryboard = new Storyboard();
            myStoryboard.Children.Add(myDoubleAnimation);
            Storyboard.SetTargetName(myDoubleAnimation, polygon.Name);
            Storyboard.SetTargetProperty(myDoubleAnimation, new PropertyPath(Polygon.OpacityProperty));
            myStoryboard.Begin(polygon);
        }

        private void ContextMenu_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            this.ContextMenu.Items.Clear();
            foreach (IMachine mac in Global.Buzz.Song.Machines)
            {
                MenuItem mi = new MenuItem() { Header = mac.Name, Tag = mac.Name };
                
                if (mac.Outputs.Count > 0)
                {
                    object dummySub = new object();
                    mi.Items.Add(dummySub);
                    mi.SubmenuOpened += Mi_SubmenuOpened;
                }
                else
                {
                    mi.Click += Mi_Click;
                }
                this.ContextMenu.Items.Add(mi);
            }
        }

        private void Mi_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            mi.Items.Clear();
            var macTarget = Global.Buzz.Song.Machines.SingleOrDefault(m => m.Name == (string)mi.Header);
            if (macTarget != null)
            {
                foreach (IMachineConnection macConn in macTarget.Outputs)
                {
                    MenuItem miConn = new MenuItem() { Header = macConn.Destination.Name, Tag = (string)mi.Header };
                    miConn.Click += Mi_Click;
                    mi.Items.Add(miConn);
                }
            }
        }

        private void Mi_Click(object sender, RoutedEventArgs e)
        {
            this.ToolTip = null;
            if (TargetConnection != null)
            {
                TargetConnection.Tap -= Machine_Tap;
                TargetConnection = null;
            }
            if (TargetMaster != null)
            {
                Global.Buzz.MasterTap -= Machine_Tap;
                TargetMaster = null;
            }
            MenuItem miSender = (MenuItem)sender;

            Init();

            var macDestination = Global.Buzz.Song.Machines.SingleOrDefault(m => m.Name == (string)miSender.Header);
            var macSource = Global.Buzz.Song.Machines.SingleOrDefault(m => m.Name == (string)miSender.Tag);
            if (macDestination != null && macSource != null)
            {
                if (macSource.Name == "Master")
                {
                    TargetMaster = macDestination;
                    Global.Buzz.MasterTap += Machine_Tap;
                    SpectrumBlockMachine.MachineState.Destination = macSource.Name;
                    SpectrumBlockMachine.MachineState.Source = macSource.Name;
                    this.ToolTip = "Master";
                }
                else
                {
                    if (macSource.Outputs.Count > 0)
                    {
                        TargetConnection = macSource.Outputs.SingleOrDefault(m => m.Destination.Name == (string)macDestination.Name);
                        TargetConnection.Tap += Machine_Tap;
                        SpectrumBlockMachine.MachineState.Destination = TargetConnection.Destination.Name;
                        SpectrumBlockMachine.MachineState.Source = TargetConnection.Source.Name;
                        this.ToolTip = TargetConnection.Source.Name + " -> " + TargetConnection.Destination.Name;
                    }
                }
            }
        }

        private void Init()
        {
            // Graw lines
            SolidColorBrush lineBrush = TryFindResource("SpectrumLineBrush") as SolidColorBrush;
            
            for (int i = 1; i < 6; i++)
            {
                double x1 = (i / 6.0) * Width;
                Line line = new Line() { X1 = x1, Y1 = 0, X2 = x1, Y2 = Height, Stroke = lineBrush, StrokeThickness = 1.0, SnapsToDevicePixels = true };
                line.IsHitTestVisible = false;
                line.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);
                this.Children.Add(line);
            }
            
            for (int currentBeat = Global.Buzz.TPB * 4; currentBeat < Pat.Length; currentBeat += Global.Buzz.TPB * 4)
            {
                double y1 = currentBeat * TickHeight;
                Line line = new Line() { X1 = 0, Y1 = y1, X2 = Width, Y2 = y1, Stroke = lineBrush, StrokeThickness = 1.0, SnapsToDevicePixels = true };
                line.IsHitTestVisible = false;
                line.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);
                this.Children.Add(line);
            }

            lowTable = new double[Pat.Length];
            midTable = new double[Pat.Length];
            highTable = new double[Pat.Length];
            volumeTable = new float[Pat.Length];
            
            polygonVol.Points.Clear();

            double paddingTop = 1;
            // Vol

            Point point = new Point();
            point.X = 0;
            point.Y = 0;
            polygonVol.Points.Add(point); // First

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0;
                point.Y = y * TickHeight + paddingTop;
                polygonVol.Points.Add(point);
            }

            point = new Point();
            point.X = 0;
            point.Y = Height;
            polygonVol.Points.Add(point); // Last

            VUMeterLevelL = 0;
            maxSampleL = -1;
            VUMeterLevelR = 0;
            maxSampleR = -1;

            // Low

            polygonLow.Points.Clear();

            point = new Point();
            point.X = 0;
            point.Y = 0;
            polygonLow.Points.Add(point); // First

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0;
                point.Y = y * TickHeight + paddingTop;
                polygonLow.Points.Add(point);
            }

            point = new Point();
            point.X = 0;
            point.Y = Height;
            polygonLow.Points.Add(point); // Last

            // Mid

            polygonMid.Points.Clear();

            point = new Point();
            point.X = 0;
            point.Y = 0;
            polygonMid.Points.Add(point); // First

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0;
                point.Y = y * TickHeight + paddingTop;
                polygonMid.Points.Add(point);
            }

            point = new Point();
            point.X = 0;
            point.Y = Height;
            polygonMid.Points.Add(point); // Last

            // High

            polygonHigh.Points.Clear();

            point = new Point();
            point.X = 0;
            point.Y = 0;
            polygonHigh.Points.Add(point); // First

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0;
                point.Y = y * TickHeight + paddingTop;
                polygonHigh.Points.Add(point);
            }

            point = new Point();
            point.X = 0;
            point.Y = Height;
            polygonHigh.Points.Add(point); // Last

            polylineBalance.Points.Clear();

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0.5 * Width;
                point.Y = y * TickHeight + paddingTop;
                polylineBalance.Points.Add(point);
            }

            point = new Point();
            point.X = 0.5 * Width;
            point.Y = Height;
            polylineBalance.Points.Add(point); // Last

            polylineCorrelationCoefficient.Points.Clear();

            for (int y = 0; y < Pat.Length; y++)
            {
                point = new Point();
                point.X = 0.9 * Width;
                point.Y = y * TickHeight + paddingTop;
                polylineCorrelationCoefficient.Points.Add(point);
            }

            point = new Point();
            point.X = 0.5 * Width;
            point.Y = Height;
            polylineCorrelationCoefficient.Points.Add(point); // Last
        }

        private void Machine_Tap(float[] samples, bool stereo, SongTime songTime)
        {
            if (!Global.Buzz.Playing || SpectrumBlockMachine.Closing)
                return;

            // Fill audio buffer
            FillAudioBuffer(samples);

            // Global.Buzz.DCWriteLine(Machine.Name + " | Current posIntick: " + 2 * songTime.PosInTick + " | Next: " + (int)(2 * songTime.PosInTick + samples.Length));

            int currentTick = songTime.CurrentTick;
            int posInTick = songTime.PosInTick;

            // Only update Spectrum when tick changed
            if (currentTick != prevTick && songTime.CurrentSubTick == 0 && songTime.PosInSubTick == 0)
            {
                prevTick = currentTick;

                ReadOnlyCollection<ISequence> seqs = SpectrumBlockMachine.GetPlayingSequences(Machine);

                foreach (ISequence seq in seqs)
                {
                    int playPosition = SpectrumBlockMachine.GetTickPositionInPattern(seq, Pat);

                    if (playPosition >= 0 && playPosition < lowTable.Length)
                    {
                        double[] audioBuf = GetBufferForFFT(posInTick);

                        double[] window = null;
                        // Apply a window?
                        switch (SpectrumBlockMachine.Window)
                        {
                            case 1:
                                window = FftSharp.Window.Hanning(audioBuf.Length);
                                break;
                            case 2:
                                window = FftSharp.Window.Hamming(audioBuf.Length);
                                break;
                            case 3:
                                window = FftSharp.Window.Blackman(audioBuf.Length);
                                break;
                            case 4:
                                window = FftSharp.Window.BlackmanExact(audioBuf.Length);
                                break;
                            case 5:
                                window = FftSharp.Window.BlackmanHarris(audioBuf.Length);
                                break;
                            case 6:
                                window = FftSharp.Window.FlatTop(audioBuf.Length);
                                break;
                            case 7:
                                window = FftSharp.Window.Bartlett(audioBuf.Length);
                                break;
                            case 8:
                                window = FftSharp.Window.Cosine(audioBuf.Length);
                                break;
                        }

                        if (window != null)
                        {
                            FftSharp.Window.ApplyInPlace(window, audioBuf);
                        }

                        // Calculate power spectral density (dB)
                        double[] fftPower = FftSharp.Transform.FFTpower(audioBuf);

                        double lowValue = GetLowSum(fftPower);
                        lowTable[playPosition] = lowValue / VUMeterRange;

                        double midValue = GetMidSum(fftPower);
                        midTable[playPosition] = midValue / VUMeterRange;

                        double highValue = GetHighSum(fftPower);
                        highTable[playPosition] = highValue / VUMeterRange;

                        UpdatePolygon(playPosition);

                        CalcVolumeMax(posInTick);
                        Point p = polylineBalance.Points[playPosition];
                        p.X = CalcStereoBalance() * (Width * 0.8) + Width * 0.1;
                        polylineBalance.Points[playPosition] = p;

                        // Correlation Coefficient
                        p = polylineCorrelationCoefficient.Points[playPosition];
                        p.X = (CorrelationCoefficient(posInTick) + 1) / 2.0 * (Width * 0.8) + Width * 0.1;
                        polylineCorrelationCoefficient.Points[playPosition] = p;

                        if (maxSampleL >= 0)
                        {
                            var db = Math.Min(Math.Max(Decibel.FromAmplitude(maxSampleL), -VUMeterRange), 0.0);
                            VUMeterLevelL = (db + VUMeterRange) / VUMeterRange;
                            maxSampleL = -1;
                        }
                        if (maxSampleR >= 0)
                        {
                            var db = Math.Min(Math.Max(Decibel.FromAmplitude(maxSampleR), -VUMeterRange), 0.0);
                            VUMeterLevelR = (db + VUMeterRange) / VUMeterRange;
                            maxSampleR = -1;
                        }

                        if (SpectrumBlockMachine.Channel == 0) // Left
                        {
                            volumeTable[playPosition] = (float)VUMeterLevelL;
                        }
                        else if (SpectrumBlockMachine.Channel == 1) // Right
                        {
                            volumeTable[playPosition] = (float)VUMeterLevelR;
                        }
                        else // Mix
                        {
                            volumeTable[playPosition] = (float)(VUMeterLevelL + VUMeterLevelR) / 2;
                        }
                        UpdateVol(playPosition);

                        break; // Draw only the first 
                    }
                }
            }
        }

        private void FillAudioBuffer(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                AudioBuffer[audioBufferFillPosition] = samples[i];
                audioBufferFillPosition++;
                audioBufferFillPosition %= AUDIO_BUFFER_SIZE;
            }
        }

        private void CalcVolumeMax(int posInTick)
        {
            int bufferSize = (int)(SpectrumBlockMachine.VolSmooth * Global.Buzz.SelectedAudioDriverSampleRate / 1000.0 );
            bufferSize = bufferSize < AUDIO_BUFFER_SIZE / 2 ? bufferSize : AUDIO_BUFFER_SIZE / 2;
    
            float[] L = new float[bufferSize];
            float[] R = new float[bufferSize];

            int readPos = (audioBufferFillPosition + AUDIO_BUFFER_SIZE - bufferSize * 2 - posInTick) % AUDIO_BUFFER_SIZE;
            
            for (int i = 0; i < bufferSize; i++)
            {
                L[i] = AudioBuffer[(2 * i + readPos) % AUDIO_BUFFER_SIZE];
                R[i] = AudioBuffer[(2 * i + 1 + readPos) % AUDIO_BUFFER_SIZE];
            }

            maxSampleL = Math.Max(maxSampleL, DSP.AbsMax(L) * (1.0f / 32768.0f));
            maxSampleR = Math.Max(maxSampleR, DSP.AbsMax(R) * (1.0f / 32768.0f));
        }

        private double CalcStereoBalance()
        { 
            double total = (maxSampleL + maxSampleR);

            if (total > 0)
                return maxSampleR / total;
            else
                return 0.5;
        }

        private void UpdatePolygon(int playPosition)
        {
            if (playPosition >= polygonLow.Points.Count)
                return;
            float zoomAll = SpectrumBlockMachine.Scale * 0.1f;

            Point lowPoint = polygonLow.Points[1 + playPosition];
            lowPoint.X = (lowTable[playPosition] * Width * zoomAll);
            polygonLow.Points[1 + playPosition] = lowPoint;

            Point midPoint = polygonMid.Points[1 + playPosition];
            midPoint.X = (midTable[playPosition] * Width * zoomAll);
            polygonMid.Points[1 + playPosition] = midPoint;

            Point highPoint = polygonHigh.Points[1 + playPosition];
            highPoint.X = (highTable[playPosition] * Width * zoomAll);
            polygonHigh.Points[1 + playPosition] = highPoint;
        }

        private void UpdateVol(int playPosition)
        {
            if (playPosition >= polygonVol.Points.Count)
                return;
            float zoomAll = SpectrumBlockMachine.Scale * 0.1f;

            Point volPoint = polygonVol.Points[1 + playPosition];
            volPoint.X = (volumeTable[playPosition] * Width * zoomAll);
            polygonVol.Points[1 + playPosition] = volPoint;
        }

        private double GetLowSum(double[] fftMag)
        {
            double fftPeriodHz = Global.Buzz.SelectedAudioDriverSampleRate / fftMag.Length / 2.0;
            int end = (int)(SpectrumBlockMachine.Low / fftPeriodHz);

            if (end == 0)
                return 0;

            double sum = 0;
            for (int i = 0; i < end && i < fftMag.Length; i++)
            {
                sum += fftMag[i];
            }

            return sum == float.NegativeInfinity ? 0 : sum / (double)end;
        }

        private double GetMidSum(double[] fftMag)
        {
            double fftPeriodHz = Global.Buzz.SelectedAudioDriverSampleRate / fftMag.Length / 2.0;

            int start = (int)(SpectrumBlockMachine.Low / fftPeriodHz);
            int end = (int)(SpectrumBlockMachine.High / fftPeriodHz);

            if (start >= end)
                return 0;

            double sum = 0;
            for (int i = start; i < end && i < fftMag.Length; i++)
            {
                if (i >= fftMag.Length)
                    break;
                sum += fftMag[i];
            }

            return sum == float.NegativeInfinity ? 0 : sum / (double)(end - start);
        }

        private double GetHighSum(double[] fftMag)
        {
            double fftPeriodHz = Global.Buzz.SelectedAudioDriverSampleRate / fftMag.Length / 2.0;

            int start = (int)(SpectrumBlockMachine.High / fftPeriodHz);
            int end = (int)(22050 / fftPeriodHz);

            if (start >= end)
                return 0;

            double sum = 0;
            for (int i = start; i < end && i < fftMag.Length; i++)
            {
                sum += fftMag[i];
            }

            return sum == float.NegativeInfinity ? 0 : sum / (double)(end - start);
        }

        private double[] GetBufferForFFT(int posInTick)
        {
            int buf_size = Global.Buzz.SelectedAudioDriverSampleRate < 88200 ? FFT_SIZE / 2 : FFT_SIZE;
            double[] ret = new double[buf_size];

            int pos = (audioBufferFillPosition + AUDIO_BUFFER_SIZE - buf_size * 2 - posInTick) % AUDIO_BUFFER_SIZE;

            int channel = this.SpectrumBlockMachine.Channel;

            for (int i = 0; i < buf_size; i++)
            {
                if (channel < 2)
                {
                    ret[i] = AudioBuffer[(pos + channel) % AUDIO_BUFFER_SIZE];
                }
                else
                {
                    ret[i] = (AudioBuffer[pos % AUDIO_BUFFER_SIZE] + AudioBuffer[(pos + 1) % AUDIO_BUFFER_SIZE]) / 2.0;
                }
                
                pos += 2;
            }

            return ret;
        }

        private void DisableEvents()
        {
            SpectrumBlockMachine.PropertyChanged -= SpectrumBlockMachine_PropertyChanged;
            graphTimer.Stop();

            Machine.GetParameter("Scale").UnsubscribeEvents(0, ScaleChanged, null);
            Machine.GetParameter("LowVisible").UnsubscribeEvents(0, LowVisibleChanged, null);
            Machine.GetParameter("MidVisible").UnsubscribeEvents(0, MidVisibleChanged, null);
            Machine.GetParameter("HighVisible").UnsubscribeEvents(0, HighVisibleChanged, null);
            Machine.GetParameter("VolumeVisible").UnsubscribeEvents(0, VolumeVisibleChanged, null);
            Machine.GetParameter("BalanceVisible").UnsubscribeEvents(0, BalanceVisibleChanged, null);
            Machine.GetParameter("CorrCoeVisible").UnsubscribeEvents(0, CorrCoeVisibleChanged, null);

            if (TargetConnection != null)
            {
                TargetConnection.Tap -= Machine_Tap;
                TargetConnection = null;
            }
            if (TargetMaster != null)
            {
                try
                {
                    Global.Buzz.MasterTap -= Machine_Tap;
                    TargetMaster = null;
                }
                catch { }
            }
        }


        private void SpectrumCanvas_Unloaded(object sender, RoutedEventArgs e)
        {
            //DisableEvents();
        }

        private double CorrelationCoefficient(int posInTick)
        {
            int bufferSize = 1024;
            bufferSize = bufferSize < AUDIO_BUFFER_SIZE / 2 ? bufferSize : AUDIO_BUFFER_SIZE / 2;

            double[] L = new double[bufferSize];
            double[] R = new double[bufferSize];

            int readPos = (audioBufferFillPosition + AUDIO_BUFFER_SIZE - bufferSize * 2 - posInTick) % AUDIO_BUFFER_SIZE;

            double sum_l = 0, sum_r = 0, sum_lr = 0, squareSum_r = 0, squareSum_l = 0;

            for (int i = 0; i < bufferSize; i++)
            {
                L[i] = AudioBuffer[(2 * i + readPos) % AUDIO_BUFFER_SIZE];
                R[i] = AudioBuffer[(2 * i + 1 + readPos) % AUDIO_BUFFER_SIZE];

                sum_l += L[i];
                sum_r += R[i];
                sum_lr += L[i] * R[i];

                squareSum_r = squareSum_r + L[i] * L[i];
                squareSum_l = squareSum_l + R[i] * R[i];
            }

            double corr = (bufferSize * sum_lr - sum_l * sum_r) /
                            (Math.Sqrt((bufferSize * squareSum_r -
                            sum_r * sum_r) * (bufferSize * squareSum_l -
                            sum_l * sum_l)));

            return double.IsNaN(corr) ? 1 : corr;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
