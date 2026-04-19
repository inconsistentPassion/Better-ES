using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace BetterES.Services
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BridgeData
    {
        // ── Header & Command Downlink (App -> DLL) ──
        // Must match SharedBridgeData layout in common.h exactly

        // uint32 fields
        public uint PacketId;
        public uint CommandBits;
        public uint CommandMethod;

        // double fields (App -> DLL)
        public double TargetRpm;
        public double Throttle;
        public double Manifold;
        public double TargetAfr;
        public double TurboPowerMultiplier;

        // Timing control (App -> DLL)
        public double AdvanceOffset;
        public double RevLimitRpm;
        public double RevLimiterCutTime;
        public double IgnitionCutPercent;

        // int32 field
        public int TargetGear;

        // uint16 fields (one-shot sequencers)
        public ushort IgnitionSeq;
        public ushort StarterSeq;
        public ushort GearSeq;
        public ushort ResetSeq;
        public ushort DynoSeq;

        // uint8 fields (flags)
        public byte TimingEnabled;
        public byte RevLimiterEnabled;
        public byte IgnitionCutEnabled;
        public byte _pad1;

        // ── Telemetry Uplink (DLL -> App) ──

        // double fields (DLL -> App)
        public double ActualRpm;
        public double ActualBoost;
        public double ActualAfr;
        public double ActualIntakeFlow;
        public double ActualTemp;
        public double ActualMaxRpm;
        public double ActualTorque;
        public double ActualSpeed;
        public double ActualClutch;
        public double ActualAdvance;

        // uint32 fields (DLL -> App)
        public uint StatusBits;
        public uint BridgeMethod;

        // int32 fields (DLL -> App)
        public int ActualGear;
        public int ActualCylinders;
    }

    public delegate void BridgeCommandDelegate(ref BridgeData data);

    public class BridgeMemoryManager : IDisposable
    {
        private const string MmfName = "BetterES_BridgeData";
        private const int MmfSize = 256; // Expanded for full control bridge

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _accessor;
        private uint _packetId = 0;

        public bool Initialize()
        {
            try
            {
                // Create or open the shared memory block
                _mmf = MemoryMappedFile.CreateOrOpen(MmfName, MmfSize, MemoryMappedFileAccess.ReadWrite);
                _accessor = _mmf.CreateViewAccessor(0, MmfSize);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Update(double rpm, double throttle, double manifold)
        {
            if (_accessor == null) return;

            var data = new BridgeData();
            _accessor.Read(0, out data);

            data.TargetRpm = rpm;
            data.Throttle = throttle;
            data.Manifold = manifold;
            data.PacketId = ++_packetId;

            _accessor.Write(0, ref data);
        }

        public void WriteCommand(BridgeCommandDelegate action)
        {
            if (_accessor == null) return;
            var data = new BridgeData();
            _accessor.Read(0, out data);
            action(ref data);
            data.PacketId = ++_packetId;
            _accessor.Write(0, ref data);
        }

        public BridgeData ReadState()
        {
            if (_accessor == null) return default;
            var data = new BridgeData();
            _accessor.Read(0, out data);
            return data;
        }

        public void SetCommandBit(int bit, bool value)
        {
            WriteCommand((ref BridgeData d) => {
                if (value) d.CommandBits |= (uint)(1 << bit);
                else d.CommandBits &= ~(uint)(1 << bit);
            });
        }

        public void IncrementSeq(int type)
        {
            WriteCommand((ref BridgeData d) => {
                if (type == 0) d.IgnitionSeq++;
                else if (type == 1) d.StarterSeq++;
                else if (type == 2) d.GearSeq++;
                else if (type == 3) d.ResetSeq++;
                else if (type == 4) d.DynoSeq++;
            });
        }

        public void Dispose()
        {
            _accessor?.Dispose();
            _mmf?.Dispose();
        }
    }
}
