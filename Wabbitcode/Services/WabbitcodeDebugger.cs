﻿using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using Revsoft.Wabbitcode.Exceptions;
using Revsoft.Wabbitcode.Services.Debugger;
using Revsoft.Wabbitcode.Services.Interface;
using Revsoft.Wabbitcode.Services.Symbols;
using Revsoft.Wabbitcode.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using WabbitemuLib;

namespace Revsoft.Wabbitcode.Services
{
	public sealed class WabbitcodeDebugger : IWabbitcodeDebugger
    {
        #region Private Fields

        private byte _appPage;
	    private ushort _lastSP;
		private IWabbitemuDebugger _debugger;
	    private bool _disposed;
		private List<KeyValuePair<ushort, ushort>> _memoryAllocations;
		
        private IBreakpoint _stepOverBreakpoint;
        private IBreakpoint _stepOutBreakpoint;
		private IBreakpoint _jforceBreakpoint;
		private IBreakpoint _ramClearBreakpoint;
		private IBreakpoint _insertMemBreakpoint;
		private IBreakpoint _delMemBreakpoint;

		private readonly ISymbolService _symbolService;
        private readonly IFileReaderService _fileReaderService;
		private readonly IDocumentService _documentService;

        #endregion

        #region Constants

        private const ushort RamCode = 0x8100;

        #endregion

        #region Public Properties

        public IZ80 CPU
		{
			get
			{
				return _debugger.CPU;
			}
		}

        public Image ScreenImage
        {
            get { return _debugger.GetScreenImage(); }
        }

	    public bool IsAnApp { get; private set; }

	    public bool IsRunning
		{
			get
			{
			    return _debugger != null && _debugger.Running;
			}
		}

	    public Stack<Tuple<ushort, ushort>> MachineStack { get; private set; }

        public Stack<Tuple<ushort, string, string, DocumentLocation>> CallStack { get; private set; }

	    #endregion

		#region Events

	    public event DebuggerRunning OnDebuggerRunningChanged;
	    public event DebuggerStep OnDebuggerStep;
		public event DebuggerClosed OnDebuggerClosed;

		#endregion

		public WabbitcodeDebugger(IDocumentService documentService,
            IFileReaderService fileReaderService, ISymbolService symbolService)
		{
		    _disposed = false;

			_documentService = documentService;
		    _fileReaderService = fileReaderService;
			_symbolService = symbolService;
		}

        #region Memory and Paging

        public byte ReadByte(ushort address)
        {
            return _debugger.Memory.ReadByte(address);
        }

        public ushort ReadShort(ushort address)
        {
            return _debugger.Memory.ReadWord(address);
        }

	    private byte GetRelativePageNum(ushort address)
		{
			IPage bank = _debugger.Memory.Bank[address >> 14];
			int page = bank.Index;
			if (bank.IsFlash)
			{
				page = _appPage - page;
			}
			return (byte)page;
		}

		private byte GetAbsolutePageNum(ushort address)
		{
			IPage bank = _debugger.Memory.Bank[address >> 14];
			int page = bank.Index;
			return (byte)page;
		}

        #endregion

        #region Startup

        public void InitDebugger(string outputFile)
		{
			_debugger = new WabbitemuDebugger();
			_debugger.LoadFile(outputFile);
			_debugger.Visible = true;
			_debugger.OnBreakpoint += BreakpointHit;
			_debugger.OnClose += DebuggerOnClose;

            IsAnApp = outputFile.EndsWith(".8xk");
			_memoryAllocations = new List<KeyValuePair<ushort, ushort>>();
            CallStack = new Stack<Tuple<ushort, string, string, DocumentLocation>>();
            MachineStack = new Stack<Tuple<ushort, ushort>>();
            SetupInternalBreakpoints();
		}

        public void CancelDebug()
        {
            IsAnApp = false;

            if (_debugger == null)
            {
                return;
            }
            _debugger.CancelDebug();
            _debugger = null;
        }

        #endregion

        #region Running

        public void Run()
        {
            if (_debugger == null)
            {
                return;
            }

            _debugger.Step();
            _debugger.Running = true;

            if (OnDebuggerRunningChanged != null)
            {
                OnDebuggerRunningChanged(this, new DebuggerRunningEventArgs(null, true));
            }
        }

		public void Pause()
		{
			_debugger.Running = false;
			ushort currentPC = _debugger.CPU.PC;
			int maxStep = 500;
			DocumentLocation key = _symbolService.ListTable.GetFileLocation(currentPC, GetRelativePageNum(currentPC), currentPC >= 0x8000);
			while (key == null && maxStep >= 0)
			{
				_debugger.Step();
				currentPC = _debugger.CPU.PC;
				key = _symbolService.ListTable.GetFileLocation(GetRelativePageNum(currentPC), currentPC, currentPC >= 0x8000);
				maxStep--;
			}

			if (maxStep < 0)
			{
				throw new DebuggingException("Unable to pause here");
			}

			if (OnDebuggerRunningChanged != null)
			{
				OnDebuggerRunningChanged(this, new DebuggerRunningEventArgs(key, false));
			}
		}

        public void SetPCToSelect(string fileName, int lineNumber)
        {
            CalcLocation value = _symbolService.ListTable.GetCalcLocation(fileName, lineNumber);
            if (value == null)
            {
                throw new Exception("Unable to set statement here!");
            }

            _debugger.CPU.PC = value.Address;
            byte page = value.Page;
            if (IsAnApp)
            {
                page = (byte)(_appPage - page);
                _debugger.Memory.Bank[1] = _debugger.Memory.Flash[page];
            }
            else
            {
                _debugger.Memory.Bank[2] = _debugger.Memory.RAM[1];
            }
        }

        public void Step()
        {
            // need to clear the old breakpoint so lets save it
            ushort currentPC = _debugger.CPU.PC;
            byte oldPage = GetRelativePageNum(currentPC);
            DocumentLocation key = _symbolService.ListTable.GetFileLocation(oldPage, currentPC, currentPC >= 0x8000);
            DocumentLocation newKey = key;
            while (newKey == null || newKey.LineNumber == key.LineNumber)
            {
                _debugger.Step();
                newKey = _symbolService.ListTable.GetFileLocation(GetRelativePageNum(_debugger.CPU.PC), _debugger.CPU.PC, _debugger.CPU.PC >= 0x8000);
            }

            ushort address = _debugger.CPU.PC;
            byte page = GetRelativePageNum(address);

            key = _symbolService.ListTable.GetFileLocation(page, address, address >= 0x8000);
            UpdateStack();

            if (OnDebuggerStep != null)
            {
                OnDebuggerStep(this, new DebuggerStepEventArgs(key));
            }
        }

        public void StepOut()
        {
            var lastCall = CallStack.Peek();
            CalcLocation calcLocation = _symbolService.ListTable.GetCalcLocation(lastCall.Item4.FileName, lastCall.Item4.LineNumber);
            DocumentLocation docLocation = null;
            ushort address = calcLocation.Address;
            while (docLocation == null)
            {
                address++;
                docLocation = _symbolService.ListTable.GetFileLocation(calcLocation.Page, address, calcLocation.IsRam);
            }
            _stepOutBreakpoint = _debugger.SetBreakpoint(calcLocation.IsRam, GetAbsolutePageNum(address), address);
            _debugger.OnBreakpoint += StepOutBreakpointEvent;
            _debugger.Step();
            _debugger.Running = true;
        }

	    private void StepOutBreakpointEvent(IWabbitemu sender, BreakpointEventArgs breakpointEventArgs)
	    {
            _debugger.ClearBreakpoint(_stepOutBreakpoint);
	        int page = GetRelativePageNum(_stepOutBreakpoint.Address.Address);
            DocumentLocation key = _symbolService.ListTable.GetFileLocation(page,
                _stepOutBreakpoint.Address.Address,
                !_stepOutBreakpoint.Address.Page.IsFlash);

            UpdateStack();
            _debugger.OnBreakpoint -= StepOutBreakpointEvent;

            if (OnDebuggerStep != null)
            {
                OnDebuggerStep(this, new DebuggerStepEventArgs(key));
            }
	    }

	    public void StepOver()
        {
            // need to clear the old breakpoint so lets save it
            ushort currentPC = _debugger.CPU.PC;
            byte oldPage = GetRelativePageNum(currentPC);
            DocumentLocation key = _symbolService.ListTable.GetFileLocation(oldPage, currentPC, currentPC >= 0x8000);

            string line = _fileReaderService.GetLine(key.FileName, key.LineNumber);

            int commentIndex = line.IndexOf(";");
            if (commentIndex != -1)
            {
                line = line.Substring(0, commentIndex);
            }

            // if the line contains a special commmand (i.e. one that will go to who knows where)
            // we just want to step over it
            string[] specialCommands = { "jp", "jr", "ret", "djnz" };
            if (specialCommands.Any(s => line.Contains(s)))
            {
                Step();
                return;
            }

            do
            {
                currentPC++;
                oldPage = GetRelativePageNum(currentPC);
                key = _symbolService.ListTable.GetFileLocation(oldPage, currentPC, currentPC >= 0x8000);
            } while (key == null);

            _stepOverBreakpoint = _debugger.SetBreakpoint(currentPC >= 0x8000, GetAbsolutePageNum(currentPC), currentPC);
            _debugger.OnBreakpoint += StepOverBreakpointEvent;

            _debugger.Step();
            _debugger.Running = true;
        }

        private void StepOverBreakpointEvent(IWabbitemu sender, EventArgs e)
        {
            _debugger.ClearBreakpoint(_stepOverBreakpoint);
            int page = _stepOverBreakpoint.Address.Page.IsFlash
                ? _appPage - _stepOverBreakpoint.Address.Page.Index
                : _stepOverBreakpoint.Address.Page.Index;
            DocumentLocation key = _symbolService.ListTable.GetFileLocation(page,
                _stepOverBreakpoint.Address.Address,
                !_stepOverBreakpoint.Address.Page.IsFlash);

            UpdateStack();
            _debugger.OnBreakpoint -= StepOverBreakpointEvent;

            if (OnDebuggerStep != null)
            {
                OnDebuggerStep(this, new DebuggerStepEventArgs(key));
            }
        }

        #endregion

        #region MachineStack

        private void UpdateCallstack()
        {
            CallStack.Clear();
            int currentSP = _debugger.CPU.SP;
            ushort topStack = (ushort) (IsAnApp ? 0xFFDF : 0xFFFF);
            while (currentSP <= topStack)
            {
                DocumentLocation location;
                Match match = CheckValidCall(ReadShort((ushort) currentSP), out location);
                if (match != null)
                {
                    string callTypeString = match.Groups["command"].Value + " " + match.Groups["condition"].Value;
                    string nameString = match.Groups["call"].Value;
                    CallStack.Push(new Tuple<ushort, string, string, DocumentLocation>(
                        (ushort) currentSP, callTypeString, nameString, location));
                }
                currentSP += 2;
            }
        }

	    private void UpdateStack()
	    {
	        Stack<Tuple<ushort, ushort>> oldStack = MachineStack;
	        MachineStack.Clear();
            int currentSP = _debugger.CPU.SP;
            ushort topStack = (ushort) (IsAnApp ? 0xFFDF : 0xFFFF);
	        while (currentSP <= topStack)
	        {
	            MachineStack.Push(new Tuple<ushort, ushort>((ushort) currentSP, ReadShort((ushort) currentSP)));
	            currentSP += 2;
	        }

            UpdateCallstack();
	    }

	    private Match CheckValidCall(ushort stackData, out DocumentLocation location)
	    {
	        location = null;
	        int page = GetRelativePageNum(stackData);
	        if (IsAnApp && (stackData < 0x4000 || stackData >= 0x8000))
	        {
	            return null;
	        }

	        DocumentLocation key;
	        do
	        {
	            key = _symbolService.ListTable.GetFileLocation(page, --stackData, stackData >= 0x8000);
	        } while (key == null);
	        string line = _fileReaderService.GetLine(key.FileName, key.LineNumber);
	        Regex callRegex = new Regex(@"\s*(?<command>\w*call)[\(?|\s]\s*((?<condition>z|nz|c|nc),\s*)?(?<call>\w*?)\)?\s*(;.*)?$",
	            RegexOptions.Compiled | RegexOptions.IgnoreCase);
	        Match match = callRegex.Match(line);
	        if (!match.Success)
	        {
	            return null;
	        }
	        location = key;
	        return match;
	    }

	    #endregion

        #region Breakpoints

        public bool SetBreakpoint(WabbitcodeBreakpoint newBreakpoint)
		{
			if (_debugger == null)
			{
				return false;
			}

			CalcLocation location = _symbolService.ListTable.GetCalcLocation(newBreakpoint.File, newBreakpoint.LineNumber + 1);
			if (location == null)
			{
				// move the breakpoint to the nearest location
				string fileName = newBreakpoint.File;
				int lineNumber = newBreakpoint.LineNumber;
				CalcLocation value = _symbolService.ListTable.GetNextNearestCalcLocation(fileName, lineNumber + 1);
				DocumentLocation newLocation = _symbolService.ListTable.GetFileLocation(value.Page, value.Address, value.IsRam);
			    WabbitcodeBreakpointManager.RemoveBreakpoint(fileName, lineNumber);
			    WabbitcodeBreakpointManager.AddBreakpoint(newLocation.FileName, newLocation.LineNumber - 1);
				return true;
			}

			newBreakpoint.Page = location.Page;
			newBreakpoint.Address = location.Address;
			newBreakpoint.IsRam = location.IsRam;
			newBreakpoint.WabbitemuBreakpoint = _debugger.SetBreakpoint(newBreakpoint.IsRam,
				(byte) (_appPage - newBreakpoint.Page), newBreakpoint.Address);
		    return false;
		}

		public void ClearBreakpoint(WabbitcodeBreakpoint newBreakpoint)
		{
			if (_debugger != null && newBreakpoint.WabbitemuBreakpoint != null)
			{
				_debugger.ClearBreakpoint(newBreakpoint.WabbitemuBreakpoint);
			}
		}

        private void BreakpointHit(object sender, BreakpointEventArgs e)
        {
            IBreakpoint breakEvent = e.Breakpoint;
            if ((breakEvent.Address == _jforceBreakpoint.Address && breakEvent.Address.Page == _jforceBreakpoint.Address.Page) ||
                (breakEvent.Address == _ramClearBreakpoint.Address && breakEvent.Address.Page == _ramClearBreakpoint.Address.Page))
            {
                DebuggerOnClose(null, EventArgs.Empty);
                return;
            }

            if (breakEvent.Address == _insertMemBreakpoint.Address && breakEvent.Address.Page == _insertMemBreakpoint.Address.Page)
            {
                _memoryAllocations.Add(new KeyValuePair<ushort, ushort>(_debugger.CPU.DE, _debugger.CPU.HL));
                _debugger.Step();
                _debugger.Running = true;
                return;
            }

            if (breakEvent.Address == _delMemBreakpoint.Address && breakEvent.Address.Page == _delMemBreakpoint.Address.Page)
            {
                _memoryAllocations.RemoveAll(kvp => kvp.Key == _debugger.CPU.HL && kvp.Value == _debugger.CPU.DE);
                _debugger.Step();
                _debugger.Running = true;
                return;
            }

            ushort address = breakEvent.Address.Address;
            int relativePage = GetRelativePageNum(address);
            WabbitcodeBreakpoint breakpoint = WabbitcodeBreakpointManager.Breakpoints.FirstOrDefault(
                b => b.Address == address && b.Page == (byte)relativePage && b.IsRam == address >= 0x8000);
            if (breakpoint == null)
            {
                return;
            }

            // throw new Exception("Breakpoint not found!");
            breakpoint.NumberOfTimesHit++;
            bool conditionsTrue = breakpoint.Enabled;
            switch (breakpoint.HitCountCondition)
            {
                case HitCountEnum.BreakEqualTo:
                    if (breakpoint.NumberOfTimesHit != breakpoint.HitCountConditionNumber)
                    {
                        conditionsTrue = false;
                    }
                    break;
                case HitCountEnum.BreakGreaterThanEqualTo:
                    if (breakpoint.NumberOfTimesHit < breakpoint.HitCountConditionNumber)
                    {
                        conditionsTrue = false;
                    }
                    break;
                case HitCountEnum.BreakMultipleOf:
                    if (breakpoint.NumberOfTimesHit % breakpoint.HitCountConditionNumber != 0)
                    {
                        conditionsTrue = false;
                    }
                    break;
            }

            if (breakpoint.BreakConditions == null)
            {
                return;
            }

            conditionsTrue = breakpoint.BreakConditions.Aggregate(conditionsTrue,
                (current, condition) => current & breakpoint.EvalCondition(_debugger.CPU, condition));

            if (conditionsTrue)
            {
                DocumentLocation key = _symbolService.ListTable.GetFileLocation(relativePage, address, !breakEvent.Address.Page.IsFlash);
                if (key == null)
                {
                    throw new Exception("Unable to find breakpoint");
                }

                UpdateStack();

                if (OnDebuggerRunningChanged != null)
                {
                    OnDebuggerRunningChanged(this, new DebuggerRunningEventArgs(key, false));
                }
            }
            else
            {
                _debugger.Running = true;
            }
        }

	    private void SetupInternalBreakpoints()
        {
            if (_debugger == null)
            {
                return;
            }

            // this is the start _JForceCmdNoChar
            const ushort jforceCmdNoChar = 0x4027;
            const ushort insertMem = 0x42F7;
            const ushort delMem = 0x4357;
            CalcLocation location = LookupBcallAddress(jforceCmdNoChar);
            _jforceBreakpoint = _debugger.SetBreakpoint(location.IsRam, location.Page, location.Address);
            // most likely location that a crash will end up
            _ramClearBreakpoint = _debugger.SetBreakpoint(false, 0, 0x0000);
            // for restarts we want to manually delmem
            location = LookupBcallAddress(insertMem);
            _insertMemBreakpoint = _debugger.SetBreakpoint(location.IsRam, location.Page, location.Address);
            // we need to track any memory freed as well
            location = LookupBcallAddress(delMem);
            _delMemBreakpoint = _debugger.SetBreakpoint(location.IsRam, location.Page, location.Address);
        }

	    private void RemoveInternalBreakpoints()
        {
            if (_debugger == null)
            {
                return;
            }

            _debugger.ClearBreakpoint(_jforceBreakpoint);
            _debugger.ClearBreakpoint(_ramClearBreakpoint);
            _debugger.ClearBreakpoint(_insertMemBreakpoint);
            _debugger.ClearBreakpoint(_delMemBreakpoint);
        }

        #endregion

        #region TIOS Specifics

        /// <summary>
		/// Returns the location of the actual code a bcall will execute.
		/// This is what TIOS does when you do a bcall.
		/// </summary>
		/// <param name="bcallAddress">The address of the bcall to map</param>
		/// <returns>The address the bcall code is located at</returns>
		private CalcLocation LookupBcallAddress(int bcallAddress)
		{
			int page;
			if ((bcallAddress & (1 << 15)) != 0)
			{
				bcallAddress &= ~(1 << 15);
				switch (_debugger.Model)
				{
					case Calc_Model.TI_73:
					case Calc_Model.TI_83P:
						page = 0x1F;
						break;
					case Calc_Model.TI_83PSE:
					case Calc_Model.TI_84PSE:
						page = 0x7F;
						break;
					case Calc_Model.TI_84P:
						page = 0x3F;
						break;
					default:
						throw new Exception("Invalid model");
				}
			}
			else if ((bcallAddress & (1 << 14)) != 0)
			{
				bcallAddress &= ~(1 << 14);
				switch (_debugger.Model)
				{
					case Calc_Model.TI_73:
					case Calc_Model.TI_83P:
						page = 0x1B;
						break;
					case Calc_Model.TI_83PSE:
					case Calc_Model.TI_84PSE:
						page = 0x7B;
						break;
					case Calc_Model.TI_84P:
						page = 0x3B;
						break;
					default:
						throw new Exception("Invalid model");
				}
			}
			else
			{
				throw new Exception("Tried looking up a local bcall");
			}

			bcallAddress += 0x4000;
			ushort realAddress = _debugger.Memory.Flash[page].ReadWord((ushort)bcallAddress);
			byte realPage = _debugger.Memory.Flash[page].ReadByte((ushort)(bcallAddress + 2));
			return new CalcLocation(realAddress, realPage, false);
		}

		public TIApplication? VerifyApp(string createdName)
		{
			if (_debugger == null || _debugger.Apps.Length == 0)
			{
				throw new DebuggingException("Application not found on calc");
			}

			char[] buffer = new char[8];
			StreamReader appReader = new StreamReader(createdName);
			for (int i = 0; i < 17; i++)
			{
				appReader.Read();
			}

			appReader.ReadBlock(buffer, 0, 8);

			appReader.Dispose();
			string appName = new string(buffer);
			TIApplication? app = _debugger.Apps.Cast<TIApplication>().SingleOrDefault(a => a.Name == appName);
			if (app == null || string.IsNullOrEmpty(app.Value.Name))
			{
				throw new DebuggingException("Application not found on calc");
			}

			_appPage = (byte)app.Value.Page.Index;
			return app;
		}

	    public void LaunchApp(string createdName)
		{
			const ushort progToEdit = 0x84BF;
			// this is code to do
			// bcall(_CloseEditBuf)
			// bcall(_ExecuteApp)
			byte[] launchAppCode = { 0xEF, 0xD3, 0x48, 0xEF, 0x51, 0x4C};
			byte[] createdNameBytes = System.Text.Encoding.ASCII.GetBytes(createdName);
			// _ExecuteApp expects the name of the app to launch in progToEdit
			_debugger.Running = false;
			_debugger.Write(true, 1, progToEdit, createdNameBytes);
			_debugger.Write(true, 1, RamCode, launchAppCode);
			_debugger.CPU.Halt = false;
			_debugger.CPU.PC = RamCode;
			_debugger.Running = true;
		}

        public void ResetRom()
        {
            RemoveInternalBreakpoints();
            _debugger.CPU.PC = 0x0000;
            Thread.Sleep(1000);
            _debugger.Keypad.PressKey(Calc_Key.KEY_ON);
            Thread.Sleep(200);
            _debugger.Keypad.ReleaseKey(Calc_Key.KEY_ON);
            SetupInternalBreakpoints();
        }

        #endregion

        private void DebuggerOnClose(IWabbitemu sender, EventArgs eventArgs)
        {
            if (OnDebuggerClosed != null)
            {
                OnDebuggerClosed(sender, eventArgs);
            }
        }


        public void GotoAddress(ushort address)
        {
            int page = GetRelativePageNum(address);
            DocumentLocation key = _symbolService.ListTable.GetFileLocation(page, address, address >= 0x8000);
            if (key == null)
            {
                return;
            }

            _documentService.GotoLine(key.FileName, key.LineNumber);
        }

		//private void StartWithoutDebuggingAssemblerFinished(object sender, AssemblyFinishEventArgs e)
		//{
		//	if (!e.AssemblySucceeded)
		//	{
		//		DockingService.ShowError("Assembly failed");
		//	}

		//	if (ProjectService.Project.ProjectOutputs.Count < 1)
		//	{
		//		DockingService.ShowError("No project outputs detected");
		//		return;
		//	}

		//	string createdName = ProjectService.Project.ProjectOutputs[0];
		//	Resources.GetResource("Wabbitemu.exe", FileLocations.WabbitemuFile);
		//	Process wabbit = Process.GetProcesses().FirstOrDefault(potential => potential.ProcessName.Contains("wabbitemu", StringComparison.OrdinalIgnoreCase));

		//	if (wabbit == null)
		//	{
		//		wabbit = new Process
		//		{
		//			StartInfo =
		//			{
		//				Arguments = "\"" + createdName + "\"",
		//				FileName = FileLocations.WabbitemuFile,
		//			},
		//			EnableRaisingEvents = true
		//		};
		//		wabbit.Start();
		//	}
        //}

        #region IDisposable

        public void Dispose()
		{
			Dispose(true);
		}

		private void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					_debugger.Dispose();
				}
			}
			_disposed = true;
        }

        #endregion
    }
}