﻿// Part of FemtoCraft | Copyright 2012 Matvei Stefarov <me@matvei.org> | See LICENSE.txt
// Based on fCraft.Player - fCraft is Copyright 2009-2012 Matvei Stefarov <me@matvei.org> | See LICENSE.fCraft.txt
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FemtoCraft {
    sealed class Player {
        readonly TcpClient client;
        NetworkStream stream;
        PacketReader reader;
        PacketWriter writer;
        readonly Thread thread;

        public IPAddress IP { get; private set; }
        public string Name { get; private set; }
        public bool IsOp { get; private set; }
        public Position Position { get; private set; }

        public bool IsRegistered { get; set; }
        public bool IsOnline { get; set; }

        const int Timeout = 10000;


        public Player( TcpClient newClient ) {
            try {
                client = newClient;
                thread = new Thread( IoThread ) {
                    IsBackground = true
                };
                thread.Start();

            } catch( Exception ex ) {
                Logger.LogError( "Player: Error setting up session: {0}", ex );
                Disconnect();
            }
        }

        bool canReceive = true,
             canSend = true,
             canQueue = true;


        void IoThread() {
            try {
                client.SendTimeout = Timeout;
                client.ReceiveTimeout = Timeout;
                IP = ( (IPEndPoint)( client.Client.RemoteEndPoint ) ).Address;
                stream = client.GetStream();
                reader = new PacketReader( stream );
                writer = new PacketWriter( stream );

                if( !LoginSequence() ) return;

                while( canSend ) {
                    // todo: poll / ping

                    // todo: position updates

                    while( canSend && sendQueue.Count > 0 ) {
                        Packet packet;
                        lock( sendQueue ) {
                            packet = sendQueue.Dequeue();
                        }
                        writer.Write( packet.Bytes );
                        if( packet.OpCode == OpCode.Kick ) {
                            writer.Flush();
                            return;
                        }
                    }

                    while( canReceive && stream.DataAvailable ) {
                        OpCode opcode = reader.ReadOpCode();
                        switch( opcode ) {
                            case OpCode.Message:
                                if( !ProcessMessagePacket() ) return;
                                break;

                            case OpCode.Teleport:
                                ProcessMovementPacket();
                                break;

                            case OpCode.SetBlockClient:
                                if( !ProcessSetBlockPacket() ) return;
                                break;

                            case OpCode.Ping:
                                continue;

                            default:
                                Logger.Log( "Player {0} was kicked after sending an invalid opcode ({1}).",
                                            Name, opcode );
                                KickNow( "Unknown packet opcode " + opcode );
                                return;
                        }
                    }
                }


            } catch( IOException ) {
            } catch( SocketException ) {
#if !DEBUG
            } catch( Exception ex ) {
                Logger.LogError( "Player: Session crashed: {0}", ex );
#endif
            } finally {
                canQueue = false;
                canSend = false;
                Disconnect();
            }
        }


        void Disconnect() {
            IsOnline = false;

            if( useSyncKick ) {
                kickWaiter.Set();
            } else {
                Server.UnregisterPlayer( this );
            }

            if( reader != null ) {
                reader.Close();
            }
            if( writer != null ) {
                writer.Close();
            }
            if( client != null ) {
                client.Close();
            }
        }


        bool LoginSequence() {
            // read the first packet
            OpCode opCode = reader.ReadOpCode();
            if( opCode != OpCode.Handshake ) {
                KickNow( "Enexpected handshake packet opcode." );
                Logger.LogWarning( "Player from {0}: Enexpected handshake packet opcode ({1})",
                                   IP, opCode );
                return false;
            }

            // check protocol version
            int protocolVersion = reader.ReadByte();
            if( protocolVersion != PacketWriter.ProtocolVersion ) {
                KickNow( "Wrong protocol version." );
                Logger.LogWarning( "Player from {0}: Wrong protocol version ({1})",
                                   IP, protocolVersion );
                return false;
            }

            // check if name is valid
            string name = reader.ReadMCString();
            if( !IsValidName( name ) ) {
                KickNow( "Unacceptible player name." );
                Logger.LogWarning( "Player from {0}: Unacceptible player name ({1})",
                                   IP, name );
                return false;
            }

            // check if name is verified
            string mppass = reader.ReadMCString();
            while( mppass.Length < 32 ) {
                mppass = "0" + mppass;
            }
            MD5 hasher = MD5.Create();
            StringBuilder sb = new StringBuilder( 32 );
            foreach( byte b in hasher.ComputeHash( Encoding.ASCII.GetBytes( Server.Salt + name ) ) ) {
                sb.AppendFormat( "{0:x2}", b );
            }
            bool verified = sb.ToString().Equals( mppass, StringComparison.OrdinalIgnoreCase );
            if( !verified ) {
                KickNow( "Could not verify player name." );
                Logger.LogWarning( "Player {0} from {1}: Could not verify name.",
                                   name, IP );
                return false;
            }
            Name = name;

            // check if player is banned
            if( Server.Bans.Contains( Name ) ) {
                KickNow( "You are banned!" );
                Logger.Log( "Banned player {0} tried to log in from {1}", Name, IP );
                return false;
            }

            // check if player's IP is banned
            if( Server.IPBans.Contains( IP ) ) {
                KickNow( "Your IP address is banned!" );
                Logger.Log( "Player {0} tried to log in from a banned IP ({1})", Name, IP );
                return false;
            }

            // skip the unused byte
            reader.ReadByte();

            // check if player is op
            IsOp = Server.Ops.Contains( Name );

            SendMap();

            IsOnline = true;
            Logger.Log( "Player {0} connected from {1}", Name, IP );
            return true;
        }


        void SendMap() {
            // write handshake
            writer.Write( OpCode.Handshake );
            writer.Write( PacketWriter.ProtocolVersion );
            writer.WriteMCString( Config.ServerName );
            writer.WriteMCString( Config.MOTD );
            writer.Write( IsOp ? (byte)100 : (byte)0 );

            // write MapBegin
            writer.Write( OpCode.MapBegin );

            // grab a compressed copy of the map
            byte[] blockData;
            Map map = Server.Map;
            using( MemoryStream mapStream = new MemoryStream() ) {
                using( GZipStream compressor = new GZipStream( mapStream, CompressionMode.Compress ) ) {
                    int convertedBlockCount = IPAddress.HostToNetworkOrder( map.Volume );
                    compressor.Write( BitConverter.GetBytes( convertedBlockCount ), 0, 4 );
                    compressor.Write( map.Blocks, 0, map.Blocks.Length );
                }
                blockData = mapStream.ToArray();
            }

            // Transfer the map copy
            byte[] buffer = new byte[1024];
            int mapBytesSent = 0;
            while( mapBytesSent < blockData.Length ) {
                int chunkSize = blockData.Length - mapBytesSent;
                if( chunkSize > 1024 ) {
                    chunkSize = 1024;
                } else {
                    // CRC fix for ManicDigger
                    for( int i = 0; i < buffer.Length; i++ ) {
                        buffer[i] = 0;
                    }
                }
                Buffer.BlockCopy( blockData, mapBytesSent, buffer, 0, chunkSize );
                byte progress = (byte)( 100 * mapBytesSent / blockData.Length );

                // write in chunks of 1024 bytes or less
                writer.Write( OpCode.MapChunk );
                writer.WriteBE( (short)chunkSize );
                writer.Write( buffer, 0, 1024 );
                writer.Write( progress );
                mapBytesSent += chunkSize;
            }

            // write MapEnd
            writer.Write( OpCode.MapEnd );
            writer.WriteBE( (short)map.Width );
            writer.WriteBE( (short)map.Height );
            writer.WriteBE( (short)map.Length );

            // write spawn point
            writer.WriteAddEntity( 255, Name, map.Spawn );
            writer.WriteTeleport( 255, map.Spawn );

            lastValidPosition = map.Spawn;
        }


        #region Send / Kick

        readonly object queueLock = new object();
        readonly Queue<Packet> sendQueue = new Queue<Packet>();

        public void Send( Packet packet ) {
            lock( queueLock ) {
                if( canQueue ) {
                    sendQueue.Enqueue( packet );
                }
            }
        }


        public void SendNow( Packet packet ) {
            writer.Write( packet.Bytes );
        }


        public void Kick( string message ) {
            Packet packet = PacketWriter.MakeDisconnect(message);
            lock( queueLock ) {
                canReceive = false;
                canQueue = false;
                sendQueue.Enqueue( packet );
            }
        }


        void KickNow( string message ) {
            canReceive = false;
            canQueue = false;
            writer.Write( OpCode.Kick );
            writer.WriteMCString( message );
        }


        bool useSyncKick;
        readonly AutoResetEvent kickWaiter = new AutoResetEvent( false );
        public void KickSynchronously( string message ) {
            lock( kickWaiter ) {
                useSyncKick = true;
                Packet packet = PacketWriter.MakeDisconnect( message );
                lock( queueLock ) {
                    canReceive = false;
                    canQueue = false;
                    sendQueue.Enqueue( packet );
                }
                kickWaiter.WaitOne();
                Server.UnregisterPlayer( this );
            }
        }

        #endregion


        #region Movement

        // anti-speedhack vars
        int speedHackDetectionCounter;
        const int AntiSpeedMaxJumpDelta = 25, // 16 for normal client, 25 for WoM
                  AntiSpeedMaxDistanceSquared = 1024, // 32 * 32
                  AntiSpeedMaxPacketCount = 200,
                  AntiSpeedMaxPacketInterval = 5;

        // anti-speedhack vars: packet spam
        readonly Queue<DateTime> antiSpeedPacketLog = new Queue<DateTime>();
        DateTime antiSpeedLastNotification = DateTime.UtcNow;
        Position lastValidPosition; // used in speedhack detection


        void ProcessMovementPacket() {
            reader.ReadByte();
            Position newPos = new Position {
                X = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                Z = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                Y = IPAddress.NetworkToHostOrder( reader.ReadInt16() ),
                R = reader.ReadByte(),
                L = reader.ReadByte()
            };

            Position oldPos = Position;

            // calculate difference between old and new positions
            Position delta = new Position {
                X = (short)( newPos.X - oldPos.X ),
                Y = (short)( newPos.Y - oldPos.Y ),
                Z = (short)( newPos.Z - oldPos.Z ),
                R = (byte)Math.Abs( newPos.R - oldPos.R ),
                L = (byte)Math.Abs( newPos.L - oldPos.L )
            };

            // skip everything if player hasn't moved
            if( delta == Position.Zero ) return;

            bool rotChanged = ( delta.R != 0 ) || ( delta.L != 0 );

            // only reset the timer if player rotated
            // if player is just pushed around, rotation does not change (and timer should not reset)
            if( rotChanged ) ResetIdleTimer();

            if( !Config.AllowSpeedHack ) {
                int distSquared = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                // speedhack detection
                if( DetectMovementPacketSpam() ) {
                    return;

                } else if( ( distSquared - delta.Z * delta.Z > AntiSpeedMaxDistanceSquared || delta.Z > AntiSpeedMaxJumpDelta ) &&
                           speedHackDetectionCounter >= 0 ) {

                    if( speedHackDetectionCounter == 0 ) {
                        lastValidPosition = Position;
                    } else if( speedHackDetectionCounter > 1 ) {
                        DenyMovement();
                        speedHackDetectionCounter = 0;
                        return;
                    }
                    speedHackDetectionCounter++;

                } else {
                    speedHackDetectionCounter = 0;
                }
            }

            Position = newPos;
        }


        bool DetectMovementPacketSpam() {
            if( antiSpeedPacketLog.Count >= AntiSpeedMaxPacketCount ) {
                DateTime oldestTime = antiSpeedPacketLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < AntiSpeedMaxPacketInterval ) {
                    DenyMovement();
                    return true;
                }
            }
            antiSpeedPacketLog.Enqueue( DateTime.UtcNow );
            return false;
        }


        void DenyMovement() {
            writer.WriteTeleport( 255, lastValidPosition );
            if( DateTime.UtcNow.Subtract( antiSpeedLastNotification ).Seconds > 1 ) {
                //todo Message( "&WYou are not allowed to speedhack." );
                antiSpeedLastNotification = DateTime.UtcNow;
            }
        }

        #endregion


        #region Block Placement

        bool PlaceWater, PlaceLava, PlaceSolid;

        readonly Queue<DateTime> spamBlockLog = new Queue<DateTime>();

        const int AntiGriefBlocks = 47;
        const int AntiGriefSeconds = 6;
        const int MaxLegalBlockType = 49;

        const int MaxBlockPlacementRange = 7 * 32;


        bool ProcessSetBlockPacket() {
            ResetIdleTimer();
            short x = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            short z = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            short y = IPAddress.NetworkToHostOrder( reader.ReadInt16() );
            ClickAction action = ( reader.ReadByte() == 1 ) ? ClickAction.Build : ClickAction.Delete;
            byte rawType = reader.ReadByte();

            // check if block type is valid
            if( rawType > MaxLegalBlockType ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} tried to place an invalid block type.", Name );
                return false;
            }
            Block block = (Block)rawType;
            if( action == ClickAction.Delete ) block = Block.Air;

            // check if coordinates are within map boundaries (dont kick)
            if( !Server.Map.InBounds( x, y, z ) ) return true;

            // check if player is close enough to place
            if( Math.Abs( x * 32 - Position.X ) > MaxBlockPlacementRange ||
                Math.Abs( y * 32 - Position.Y ) > MaxBlockPlacementRange ||
                Math.Abs( z * 32 - Position.Z ) > MaxBlockPlacementRange ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} tried to place a block too far away.", Name );
                return false;
            }

            // check click rate
            if( Config.LimitClickRate && DetectBlockSpam() ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} tried to place blocks too quickly.", Name );
                return false;
            }

            // apply blocktype mapping
            if( block == Block.Blue && PlaceWater ) {
                block = Block.Water;
            } else if( block == Block.Red && PlaceLava ) {
                block = Block.Water;
            } else if( block == Block.Stone && PlaceSolid ) {
                block = Block.Admincrete;
            }

            // check if blocktype is permitted
            if( ( block == Block.Water || block == Block.Lava || block == Block.Admincrete ||
                  block == Block.StillWater || block == Block.StillLava ) && !IsOp ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} tried to place a restricted block type.", Name );
                return false;
            }

            // check if deleting admincrete
            Block oldBlock = Server.Map.GetBlock( x, y, z );
            if( oldBlock == Block.Admincrete && !IsOp ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} tried to delete a restricted block type.", Name );
                return false;
            }

            // update map
            // todo: queue for physics processing
            Server.Map.SetBlock( x, y, z, block );
            if( (byte)block != rawType ) {
                writer.WriteSetBlock( x, y, z, block );
            }
            return true;
        }


        bool DetectBlockSpam() {
            if( spamBlockLog.Count >= AntiGriefBlocks ) {
                DateTime oldestTime = spamBlockLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < AntiGriefSeconds ) {
                    return true;
                }
            }
            spamBlockLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion


        #region Messaging

        bool ProcessMessagePacket() {
            ResetIdleTimer();
            reader.ReadByte();
            string message = reader.ReadMCString();

            if( message.StartsWith( "/womid " ) ) {
                return true;
            }

            if( MessageHandler.ContainsInvalidChars( message ) ) {
                KickNow( "Hacking detected." );
                Logger.Log( "Player {0} attempted to write illegal characters in chat and was kicked.",
                            Name );
                return false;
            }

            MessageHandler.Parse( this, message );
            return true;
        }

        #endregion


        // todo: use for admin-slot kicks
        public DateTime LastActiveTime { get; private set; }

        void ResetIdleTimer() {
            LastActiveTime = DateTime.UtcNow;
        }


        public static bool IsValidName( string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            if( name.Length < 2 || name.Length > 16 ) return false;
            return name.All( ch => ( ch >= '0' || ch == '.' ) &&
                                   ( ch <= '9' || ch >= 'A' ) &&
                                   ( ch <= 'Z' || ch >= '_' ) &&
                                   ( ch <= '_' || ch >= 'a' ) &&
                                   ch <= 'z' );
        }
    }
}