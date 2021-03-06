//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Storage;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using System.IO;
using Antmicro.Renode.Exceptions;
using static Antmicro.Renode.Utilities.BitHelper;

namespace Antmicro.Renode.Peripherals.SD
{
    // Features NOT supported:
    // * Toggling selected state
    // * RCA (relative card address) filtering
    // As a result any SD controller with more than one SD card attached at the same time might not work properly.
    public class SDCard : IPeripheral
    {
        public SDCard(string imageFile, long? size = null, bool persistent = false)
        {
            dataBackend = DataStorage.Create(imageFile, size, persistent);

            cardStatusGenerator = new VariableLengthValue(32)
                .DefineFragment(5, 1, () => (treatNextCommandAsAppCommand ? 1 : 0u), name: "APP_CMD bit")
                .DefineFragment(8, 1, 1, name: "READ_FOR_DATA bit");

            operatingConditionsGenerator = new VariableLengthValue(32)
                .DefineFragment(31, 1, 1, name: "Card power up status bit (busy)")
            ;

            cardSpecificDataGenerator = new VariableLengthValue(128)
                .DefineFragment(47, 3, (uint)SizeMultiplier.Multiplier512, name: "device size multiplier")
                .DefineFragment(62, 12, 0xFFF, name: "device size")
                .DefineFragment(80, 4, (uint)BlockLength.Block2048, name: "max read data block length")
                .DefineFragment(84, 12, (uint)CardCommandClass.Class0, name: "card command classes")
                .DefineFragment(96, 3, (uint)TransferRate.Transfer10Mbit, name: "transfer rate unit")
                .DefineFragment(99, 4, (uint)TransferMultiplier.Multiplier2_5, name: "transfer multiplier")
            ;

            cardIdentificationGenerator = new VariableLengthValue(128)
                .DefineFragment(8, 4, 8, name: "manufacturer date code - month")
                .DefineFragment(12, 8, 18, name: "manufacturer date code - year")
                .DefineFragment(64, 8, (uint)'D', name: "product name 5")
                .DefineFragment(72, 8, (uint)'O', name: "product name 4")
                .DefineFragment(80, 8, (uint)'N', name: "product name 3")
                .DefineFragment(88, 8, (uint)'E', name: "product name 2")
                .DefineFragment(96, 8, (uint)'R', name: "product name 1")
                .DefineFragment(120, 8, 0xab, name: "manufacturer ID")
            ;
        }

        public void Reset()
        {
            readContext.Reset();
            writeContext.Reset();
            treatNextCommandAsAppCommand = false;
        }

        public void Dispose()
        {
            dataBackend.Dispose();
        }

        public BitStream HandleCommand(uint commandIndex, uint arg)
        {
            BitStream result;
            this.Log(LogLevel.Debug, "Command received: 0x{0:x} with arg 0x{1:x}", commandIndex, arg);
            var treatNextCommandAsAppCommandLocal = treatNextCommandAsAppCommand;
            treatNextCommandAsAppCommand = false;
            if(!treatNextCommandAsAppCommandLocal || !TryHandleApplicationSpecificCommand((SdCardApplicationSpecificCommand)commandIndex, arg, out result))
            {
                result = HandleStandardCommand((SdCardCommand)commandIndex, arg);
            }
            this.Log(LogLevel.Debug, "Sending command response: {0}", result.ToString());
            return result;
        }

        public void WriteData(byte[] data)
        {
            if(!writeContext.IsActive || writeContext.Data != null)
            {
                this.Log(LogLevel.Warning, "Trying to write data when the SD card is not expecting it");
                return;
            }
            if(!writeContext.CanAccept((uint)data.Length))
            {
                this.Log(LogLevel.Warning, "Trying to write more data ({0} bytes) than expected ({1} bytes). Ignoring the whole transfer", data.Length, writeContext.BytesLeft);
                return;
            }
            WriteDataToUnderlyingFile(writeContext.Offset, data.Length, data);
            writeContext.Move((uint)data.Length);
        }

        // TODO: this method should be removed and it should be controller's responsibility to control the number of bytes to read
        public void SetReadLimit(uint size)
        {
            this.Log(LogLevel.Debug, "Setting read limit to: {0}", size);
            readContext.BytesLeft = size;
        }

        public byte[] ReadData(uint size)
        {
            if(!readContext.IsActive)
            {
                this.Log(LogLevel.Warning, "Trying to read data when the SD card is not expecting it");
                return new byte[0];
            }
            if(!readContext.CanAccept(size))
            {
                this.Log(LogLevel.Warning, "Trying to read more data ({0} bytes) than expected ({1} bytes). Ignoring the whole transfer", size, readContext.BytesLeft);
                return new byte[0];
            }

            byte[] result;
            if(readContext.Data != null)
            {
                result = readContext.Data.AsByteArray(readContext.Offset, size);
                readContext.Move(size * 8);
            }
            else
            {
                result = ReadDataFromUnderlyingFile(readContext.Offset, checked((int)size));
                readContext.Move(size);
            }
            return result;
        }

        public bool IsReadyForWritingData => writeContext.IsActive;

        public bool IsReadyForReadingData => readContext.IsActive;

        public ushort CardAddress { get; set; }

        public BitStream CardStatus => cardStatusGenerator.Bits;

        public BitStream OperatingConditions => operatingConditionsGenerator.Bits;

        public BitStream SDConfiguration => new VariableLengthValue(64).Bits;

        public BitStream SDStatus => new VariableLengthValue(512).Bits;

        public BitStream CardSpecificData => cardSpecificDataGenerator.GetBits(skip: 8);

        public BitStream CardIdentification => cardIdentificationGenerator.GetBits(skip: 8);

        private void WriteDataToUnderlyingFile(long offset, int size, byte[] data)
        {
            dataBackend.Position = offset;
            var actualSize = checked((int)Math.Min(size, dataBackend.Length - dataBackend.Position));
            if(actualSize < size)
            {
                this.Log(LogLevel.Warning, "Tried to write {0} bytes of data to offset {1}, but space for only {2} is available.", size, offset, actualSize);
            }
            dataBackend.Write(data, 0, actualSize);
        }

        private byte[] ReadDataFromUnderlyingFile(long offset, int size)
        {
            dataBackend.Position = offset;
            var actualSize = checked((int)Math.Min(size, dataBackend.Length - dataBackend.Position));
            if(actualSize < size)
            {
                this.Log(LogLevel.Warning, "Tried to read {0} bytes of data from offset {1}, but only {2} is available.", size, offset, actualSize);
            }
            var result = new byte[actualSize];
            var readSoFar = 0;
            while(readSoFar < actualSize)
            {
                var readThisTime = dataBackend.Read(result, readSoFar, actualSize - readSoFar);
                if(readThisTime == 0)
                {
                    // this should not happen as we calculated the available data size
                    throw new ArgumentException("Unexpected end of data in file stream");
                }
                readSoFar += readThisTime;
            }

            return result;
        }

        private BitStream HandleStandardCommand(SdCardCommand command, uint arg)
        {
            this.Log(LogLevel.Debug, "Handling as a standard command: {0}", command);
            switch(command)
            {
                case SdCardCommand.GoIdleState_CMD0:
                    Reset();
                    return BitStream.Empty;

                case SdCardCommand.SendCardIdentification_CMD2:
                    return CardIdentification;

                case SdCardCommand.SendRelativeAddress_CMD3:
                {
                    var status = CardStatus.AsUInt32();
                    return BitHelper.BitConcatenator.New()
                        .StackAbove(status, 13, 0)
                        .StackAbove(status, 1, 19)
                        .StackAbove(status, 2, 22)
                        .StackAbove(CardAddress, 16, 0)
                        .Bits;
                }

                case SdCardCommand.SelectDeselectCard_CMD7:
                    return CardStatus;

                case SdCardCommand.SendCardSpecificData_CMD9:
                    return CardSpecificData;

                case SdCardCommand.StopTransmission_CMD12:
                    readContext.Reset();
                    writeContext.Reset();
                    return CardStatus;

                case SdCardCommand.SendStatus_CMD13:
                    return CardStatus;

                case SdCardCommand.SetBlockLength_CMD16:
                    blockLengthInBytes = arg;
                    return CardStatus;

                case SdCardCommand.ReadSingleBlock_CMD17:
                    readContext.Offset = arg;
                    readContext.BytesLeft = blockLengthInBytes;
                    return CardStatus;

                case SdCardCommand.ReadMultipleBlocks_CMD18:
                    readContext.Offset = arg;
                    return CardStatus;

                case SdCardCommand.WriteSingleBlock_CMD24:
                    writeContext.Offset = arg;
                    writeContext.BytesLeft = blockLengthInBytes;
                    return CardStatus;

                case SdCardCommand.AppCommand_CMD55:
                    treatNextCommandAsAppCommand = true;
                    return CardStatus;

                default:
                    this.Log(LogLevel.Warning, "Unsupported command: {0}. Ignoring it", command);
                    return BitStream.Empty;
            }
        }

        private bool TryHandleApplicationSpecificCommand(SdCardApplicationSpecificCommand command, uint arg, out BitStream result)
        {
            this.Log(LogLevel.Debug, "Handling as an application specific command: {0}", command);
            switch(command)
            {
                case SdCardApplicationSpecificCommand.SendSDCardStatus_ACMD13:
                    readContext.Data = SDStatus;
                    result = CardStatus;
                    return true;

                case SdCardApplicationSpecificCommand.SendOperatingConditionRegister_ACMD41:
                    result = OperatingConditions;
                    return true;

                case SdCardApplicationSpecificCommand.SendSDConfigurationRegister_ACMD51:
                    readContext.Data = SDConfiguration;
                    result = CardStatus;
                    return true;

                default:
                    this.Log(LogLevel.Debug, "Command #{0} seems not to be any application specific command", command);
                    result = null;
                    return false;
            }
        }

        private bool treatNextCommandAsAppCommand;
        private uint blockLengthInBytes;
        private IoContext writeContext;
        private IoContext readContext;
        private readonly Stream dataBackend;
        private readonly VariableLengthValue cardStatusGenerator;
        private readonly VariableLengthValue operatingConditionsGenerator;
        private readonly VariableLengthValue cardSpecificDataGenerator;
        private readonly VariableLengthValue cardIdentificationGenerator;

        private struct IoContext
        {
            public uint Offset
            {
                get { return offset; }
                set
                {
                    offset = value;
                    data = null;
                }
            }

            public BitStream Data
            {
                get { return data; }
                set
                {
                    data = value;
                    offset = 0;
                }
            }

            public uint BytesLeft
            {
                get
                {
                    if(data != null)
                    {
                        return (data.Length - offset) / 8;
                    }

                    return bytesLeft;
                }

                set
                {
                    if(data != null && BytesLeft > 0)
                    {
                        throw new ArgumentException("Setting bytes left in data mode is not supported");
                    }

                    bytesLeft = value;
                }
            }

            public bool IsActive => BytesLeft > 0;

            public bool CanAccept(uint size)
            {
                return BytesLeft >= size;
            }

            public void Move(uint offset)
            {
                this.offset += offset;
                if(data == null)
                {
                    bytesLeft -= offset;
                }
            }

            public void Reset()
            {
                offset = 0;
                bytesLeft = 0;
                data = null;
            }

            private uint bytesLeft;
            private uint offset;
            private BitStream data;
        }

        private enum SdCardCommand
        {
            GoIdleState_CMD0 = 0,
            SendCardIdentification_CMD2 = 2,
            SendRelativeAddress_CMD3 = 3,
            SelectDeselectCard_CMD7 = 7,
            // this command has been added in spec version 2.0 - we don't have to answer to it
            SendInterfaceConditionCommand_CMD8 = 8,
            SendCardSpecificData_CMD9 = 9,
            StopTransmission_CMD12 = 12,
            SendStatus_CMD13 = 13,
            SetBlockLength_CMD16 = 16,
            ReadSingleBlock_CMD17 = 17,
            ReadMultipleBlocks_CMD18 = 18,
            WriteSingleBlock_CMD24 = 24,
            AppCommand_CMD55 = 55,
        }

        private enum SdCardApplicationSpecificCommand
        {
            SendSDCardStatus_ACMD13 = 13,
            SendOperatingConditionRegister_ACMD41 = 41,
            SendSDConfigurationRegister_ACMD51 = 51
        }

        private enum SizeMultiplier
        {
            Multiplier4 = 0,
            Multiplier8 = 1,
            Multiplier16 = 2,
            Multiplier32 = 3,
            Multiplier64 = 4,
            Multiplier128 = 5,
            Multiplier256 = 6,
            Multiplier512 = 7
        }

        private enum BlockLength
        {
            Block512 = 9,
            Block1024 = 10,
            Block2048 = 11
            // other values are reserved
        }

        [Flags]
        private enum CardCommandClass
        {
            Class0 = (1 << 0),
            Class1 = (1 << 1),
            Class2 = (1 << 2),
            Class3 = (1 << 3),
            Class4 = (1 << 4),
            Class5 = (1 << 5),
            Class6 = (1 << 6),
            Class7 = (1 << 7),
            Class8 = (1 << 8),
            Class9 = (1 << 9),
            Class10 = (1 << 10),
            Class11 = (1 << 11)
        }

        private enum TransferRate
        {
            Transfer100kbit = 0,
            Transfer1Mbit = 1,
            Transfer10Mbit = 2,
            Transfer100Mbit = 3,
            // the rest is reserved
        }

        private enum TransferMultiplier
        {
            Reserved = 0,
            Multiplier1 = 1,
            Multiplier1_2 = 2,
            Multiplier1_3 = 3,
            Multiplier1_5 = 4,
            Multiplier2 = 5,
            Multiplier2_5 = 6,
            Multiplier3 = 7,
            Multiplier3_5 = 8,
            Multiplier4 = 9,
            Multiplier4_5 = 10,
            Multiplier5 = 11,
            Multiplier5_5 = 12,
            Multiplier6 = 13,
            Multiplier7 = 14,
            Multiplier8 = 15
        }
    }
}
