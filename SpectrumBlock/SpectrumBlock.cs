using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;
using ModernSequenceEditor.Interfaces;

namespace SpectrumBlock
{
    [MachineDecl(Name = "Spectrum Block", ShortName = "SpectrumBlock", Author = "WDE", MaxTracks = 1)]
	public class SpectrumBlockMachine : IBuzzMachine, INotifyPropertyChanged, IModernSequencerMachineInterface
	{
		internal IBuzzMachineHost host;
		
		public SpectrumBlockMachine(IBuzzMachineHost host)
		{
			this.host = host;
			Global.Buzz.Song.MachineAdded += Song_MachineAdded;
            Global.Buzz.Song.MachineRemoved += Song_MachineRemoved;
		}

        private void Song_MachineRemoved(IMachine obj)
        {
			if (host.Machine == obj)
			{
				Closing = true;
				if (PropertyChanged != null) PropertyChanged.Raise(this, "Closing");
				Global.Buzz.Song.MachineAdded -= Song_MachineAdded;
			}
		}

        private void Song_MachineAdded(IMachine obj)
		{
			if (host.Machine == obj)
			{
				Closing = false;
			}
		}

        [ParameterDecl(DefValue = 2, MinValue = 0, MaxValue = 2, ValueDescriptions = new[] { "Left", "Right", "Mix" })]
		public int Channel { get; set; }

		[ParameterDecl(DefValue = 0, MinValue =  0, MaxValue = 8, ValueDescriptions = new[] { "None", "Hanning", "Hamming", "Blackman", "BlackmanExact", "BlackmanHarris", "FlatTop", "Bartlett", "Cosine" }, Description = "Apply windowing to the audio prior to FFT analysis.")]
		public int Window { get; set; }

		[ParameterDecl(DefValue = 300, MinValue = 0, MaxValue = 22050, Name = "Low (Hz)", Description = "Spectrum frequency low range ceiling Hz.")]
		public int Low { get; set; }

		[ParameterDecl(DefValue = 2400, MinValue = 0, MaxValue = 22050, Name = "High (Hz)", Description = "Spectrum frequency high range floor Hz.")]
		public int High { get; set; }

		private bool lowVisible;
		[ParameterDecl(DefValue = true, ValueDescriptions = new[] { "No", "Yes" })]
		public bool LowVisible { get => lowVisible; set { lowVisible = value; } }

        private bool midVisible;
		[ParameterDecl(DefValue = true, ValueDescriptions = new[] { "No", "Yes" })]
		public bool MidVisible { get => midVisible; set { midVisible = value; } }

		private bool highVisible;
		[ParameterDecl(DefValue = true, ValueDescriptions = new[] { "No", "Yes" })]
		public bool HighVisible { get => highVisible; set { highVisible = value; } }

		private bool volVisible;
		[ParameterDecl(DefValue = true, ValueDescriptions = new[] { "No", "Yes" })]
		public bool VolumeVisible { get => volVisible; set { volVisible = value; } }


		[ParameterDecl(DefValue = true, Description = "Show Stereo Balance", ValueDescriptions = new[] { "No", "Yes" })]
		public bool BalanceVisible { get; set; }

		[ParameterDecl(DefValue = true, Description = "Show Correlation Coefficient", ValueDescriptions = new[] { "No", "Yes" })]
		public bool CorrCoeVisible { get; set; }

		[ParameterDecl(DefValue = 10, MinValue = 1, MaxValue = 100, Name = "VolSmooth (ms)", Description ="Volume graph buffer length (ms)")]
		public int VolSmooth { get; set; }

		private int scale;
		[ParameterDecl(DefValue = 10, MinValue = 1, MaxValue = 100)]
		public int Scale { get => scale; set { scale = value; } }


        // actual machine ends here. the stuff below demonstrates some other features of the api.

        public class State : INotifyPropertyChanged
		{
			public State() { destination = ""; Source = ""; }	// NOTE: parameterless constructor is required by the xml serializer

			string destination;
			public string Destination 
			{
				get { return destination; }
				set
				{
					destination = value;
					//if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Text"));
					// NOTE: the INotifyPropertyChanged stuff is only used for data binding in the GUI in this demo. it is not required by the serializer.
				}
			}

			string source;
            public string Source
			{
				get { return source; }
				set
				{
					source = value;
				}
			}

            public event PropertyChangedEventHandler PropertyChanged;
		}

		State machineState = new State();
        private string propertyTargetName;

        public State MachineState			// a property called 'MachineState' gets automatically saved in songs and presets
		{
			get { return machineState; }
			set
			{
				machineState = value;
				if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("MachineState"));
			}
		}		
		
		
		public IEnumerable<IMenuItem> Commands
		{
			get
			{
				yield return new MenuItemVM() 
				{ 
					Text = "About...", 
					Command = new SimpleCommand()
					{
						CanExecuteDelegate = p => true,
						ExecuteDelegate = p => MessageBox.Show("SpectrumBlock 0.4 (C) WDE\n\nUses FftSharp - a collection of Fast Fourier Transform (FFT) tools for .NET")
					}
				};
			}
		}

        public bool Closing { get; private set; }
       

        public event PropertyChangedEventHandler PropertyChanged;

        public Canvas PrepareCanvasForSequencer(IPattern pat, SequencerLayout layout, double tickHeight, int pos, double width, double height)
        {
			SpectrumCanvas sc = null;

			if (pat.Machine == host.Machine)
			{	
				if (layout == SequencerLayout.Vertical)
				{
					sc = new SpectrumCanvas(this, pat, tickHeight, width, height);
				}
			}
			return sc;
		}

		public double GetPatternLenghtInSeconds(IPattern pat)
		{
			double ret = 0;
			if (pat != null)
			{
				double ticksPerSecond = (double)host.MasterInfo.BeatsPerMin / 60.0 * (double)host.MasterInfo.TicksPerBeat;
				ret = (double)pat.Length / ticksPerSecond;
			}

			return ret;
		}

		public int GetTickPositionInPattern(ISequence seq, IPattern pat)
		{	
			if (seq.Machine.Name == this.host.Machine.Name)
			{
				if (pat != null && pat.PlayPosition >= 0 && pat == seq.PlayingPattern)
				{	
					return pat.PlayPosition / PatternEvent.TimeBase;
				}
				else return -1;
			}
			return -1;
		}

		public ReadOnlyCollection<ISequence> GetPlayingSequences(IMachine machine)
		{
			List<ISequence> sequences = new List<ISequence>();
			foreach (ISequence seq in host.Machine.Graph.Buzz.Song.Sequences)
			{
				if (seq.Machine.Name == machine.Name)
				{
					IPattern pat = seq.PlayingPattern;
					if (pat != null)
					{
						sequences.Add(seq);
					}
				}
			}
			return sequences.AsReadOnly();
		}
	}
}
