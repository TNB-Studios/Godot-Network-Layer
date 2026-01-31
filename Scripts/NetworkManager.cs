using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Godot;
using Godot.Collections;

public class NetworkManager
{
    // Reference nodes for scene tree access
    private Node _serverSceneNode;
    private Node _clientSceneNode;

    // common to both server and client


 
    // NOTE - On the server these need to be precached BEFORE we start the game; they cannot be changed post game start, since they are transmitted
    // to all clients on each Client being initialized. Also, they can't ba larger than 65536 in size, but that should be enough for anyone - this is the max index size in individual SharedProperties
    public List<string> SoundsUsed { get; set; } = new List<string>();
    public List<string> ModelsUsed { get; set; } = new List<string>();
    public List<string> AnimationsUsed { get; set; } = new List<string>();
    public List<string> ParticleEffectsUsed {get; set;} = new List<string>();


    private HashedSlotArray IDToNetworkIDLookup;

    // Client Side specific
    private int FramesFromServerCount = 0;
    NetworkingPlayerState ClientPlayer;

    // Server Side Specific
    private int FrameCount = 0;
    public List<FrameState> Frames { get; set; } 
    private List<short> Node3DIDsDeletedThisFrame;
    public List<NetworkingPlayerState> Players { get; set; }  // note, on the server, this will have mulitple entries.

    private static readonly string NETWORKED_GROUP_NAME = "networked";

    private bool gameStarted = false;

    // Callback delegate for game-specific data to be written to TCP init packet (server-side)
    public unsafe delegate int GameSpecificDataWriter(byte* bufferPtr, int bufferSize);
    private GameSpecificDataWriter gameSpecificDataWriterCallback = null;

    // Callback delegate for game-specific data to be read from TCP init packet (client-side)
    public unsafe delegate int GameSpecificDataReader(byte* bufferPtr, int bufferSize);
    private GameSpecificDataReader gameSpecificDataReaderCallback = null;

    public NetworkManager(Node serverSceneNode, Node clientSceneNode)
    {
        _serverSceneNode = serverSceneNode;
        _clientSceneNode = clientSceneNode;
    }


    /// <summary>
    /// Registers a callback to write game-specific data at the start of the TCP init packet.
    /// The callback receives a buffer pointer and size, and returns bytes written.
    /// Server-side only.
    /// </summary>
    public void RegisterGameSpecificDataWriterCallback(GameSpecificDataWriter callback)
    {
        gameSpecificDataWriterCallback = callback;
    }

    /// <summary>
    /// Registers a callback to read game-specific data from the start of the TCP init packet.
    /// The callback receives a buffer pointer and size, and returns bytes read.
    /// Client-side only.
    /// </summary>
    public void RegisterGameSpecificDataReaderCallback(GameSpecificDataReader callback)
    {
        gameSpecificDataReaderCallback = callback;
    }

    public void AddPrecacheAnimationUsed_Server(string animationName)
    {
        Debug.Assert(!gameStarted, "Game Started. Can't be adding Animations used at this point!!");   
        AnimationsUsed.Add(animationName);
    }

    public void AddPrecacheModelsUsed_Server(string modelName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Models used at this point!!");   
        ModelsUsed.Add(modelName);
    }

    public void AddPrecacheSoundsUsed_Server(string soundsName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Sounds used at this point!!");
        SoundsUsed.Add(soundsName);
    }

    public void AddPrecacheParticleEffectsUsed_Server(string soundsName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Particle Effects used at this point!!");
        SoundsUsed.Add(soundsName);
    }

    public void AddNode3DToNetworkedNodeList_Server(Node3D newNode)
    {
        newNode.AddToGroup(NETWORKED_GROUP_NAME);
        IDToNetworkIDLookup.Insert(newNode.GetInstanceId());
    }

    public void RemoveNode3DfromNetworkNodeList_Server(Node3D nodeToRemove)
    {
        short indexOfObjectBeingRemoved = IDToNetworkIDLookup.Find(nodeToRemove.GetInstanceId());
        Node3DIDsDeletedThisFrame.Add(indexOfObjectBeingRemoved);
    }

    private const int TCP_INIT_PACKET_SIZE = 65536;

    public void NewGameSetup_Server(int playerCount)
    {
        // note, not reseting the arrays of animations, models and sounds used, since this shouldn't change game to game.
        Players = new List<NetworkingPlayerState>();
        Frames = new List<FrameState>();

        // Create a PlayerState for each player
        for (int i = 0; i < playerCount; i++)
        {
            NetworkingPlayerState newPlayer = new NetworkingPlayerState();
            newPlayer.WhichPlayerAreWeOnServer = i;
            Players.Add(newPlayer);
        }

        Node3DIDsDeletedThisFrame = new List<short>();

        FrameCount = 0;

        // zeroed by default.
        IDToNetworkIDLookup = new HashedSlotArray();

        gameStarted = true;

        // Create the TCP initialization packet with all precache data and initial object states
        byte[] tcpInitPacket = new byte[TCP_INIT_PACKET_SIZE];
        int currentOffset = 0;
        int playerIndexOffset = 0; // Will store the offset of the player index byte

        unsafe
        {
            fixed (byte* bufferPtr = tcpInitPacket)
            {
                // First, call the game-specific data callback if registered
                if (gameSpecificDataWriterCallback != null)
                {
                    int gameSpecificBytesWritten = gameSpecificDataWriterCallback(bufferPtr, TCP_INIT_PACKET_SIZE);
                    currentOffset += gameSpecificBytesWritten;
                }

                // Write player index (1 byte) - placeholder, will be updated per player
                playerIndexOffset = currentOffset;
                bufferPtr[currentOffset] = 0;
                currentOffset++;

                // Write SoundsUsed list
                currentOffset = WriteStringListToBuffer(bufferPtr, currentOffset, TCP_INIT_PACKET_SIZE, SoundsUsed);

                // Write ModelsUsed list
                currentOffset = WriteStringListToBuffer(bufferPtr, currentOffset, TCP_INIT_PACKET_SIZE, ModelsUsed);

                // Write AnimationsUsed list
                currentOffset = WriteStringListToBuffer(bufferPtr, currentOffset, TCP_INIT_PACKET_SIZE, AnimationsUsed);

                // Write ParticleEffectsUsed list
                currentOffset = WriteStringListToBuffer(bufferPtr, currentOffset, TCP_INIT_PACKET_SIZE, ParticleEffectsUsed);

                // Create initial FrameState from all current objects
                FrameState initialFrameState = CreateFrameStateFromCurrentObjects_Server(FrameCount);

                // Write frame index (3 bytes) - this will be frame 1 (first frame)
                Debug.Assert(currentOffset + 3 <= TCP_INIT_PACKET_SIZE, "Buffer overflow writing initial frame index");
                WriteInt24ToBuffer(bufferPtr, currentOffset, FrameCount + 1);
                currentOffset += 3;

                // Write object count
                Debug.Assert(currentOffset + sizeof(short) <= TCP_INIT_PACKET_SIZE, "Buffer overflow writing initial object count");
                *(short*)(bufferPtr + currentOffset) = (short)initialFrameState.SharedObjects.Count;
                currentOffset += sizeof(short);

                // Write all SharedProperties to the buffer (no player culling, no delta compression)
                var (bytesWritten, objectCount) = WriteSharedPropertiesToBuffer_Server(
                    bufferPtr,
                    currentOffset,
                    TCP_INIT_PACKET_SIZE,
                    initialFrameState,
                    null,  // no lastAckedFrameState - this is the initial state
                    null); // no player - skip visibility culling, send all objects

                currentOffset += bytesWritten;

                // Store this as frame 0 - the known initial state all clients will have
                Frames.Add(initialFrameState);
                FrameCount++;

                // Send the TCP packet to each player, updating the player index for each
                for (int playerIndex = 0; playerIndex < playerCount; playerIndex++)
                {
                    // Update the player index byte in the buffer
                    bufferPtr[playerIndexOffset] = (byte)playerIndex;

                    // Send the packet to this player
                    SendTCPInitPacketToPlayer_Server(tcpInitPacket, currentOffset, playerIndex);
                }
            }
        }
    }

    /// <summary>
    /// Sends the TCP initialization packet to a specific player.
    /// Override or implement actual network transmission.
    /// </summary>
    protected virtual void SendTCPInitPacketToPlayer_Server(byte[] packet, int packetSize, int playerIndex)
    {
        // TODO: Implement actual TCP transmission to the player
        // This is a stub that can be overridden or replaced with actual networking code
    }

    /// <summary>
    /// Writes a list of strings to a buffer with count prefix and null terminators.
    /// Format: [count: short][string1\0][string2\0]...
    /// Returns the new offset after writing.
    /// </summary>
    private unsafe int WriteStringListToBuffer(byte* bufferPtr, int offset, int bufferSize, List<string> stringList)
    {
        // Write count as short
        Debug.Assert(offset + sizeof(short) <= bufferSize, "Buffer overflow writing string list count");
        *(short*)(bufferPtr + offset) = (short)stringList.Count;
        offset += sizeof(short);

        // Write each string with null terminator
        foreach (string str in stringList)
        {
            byte[] strBytes = System.Text.Encoding.UTF8.GetBytes(str);
            Debug.Assert(offset + strBytes.Length + 1 <= bufferSize, $"Buffer overflow writing string: {str}");
            foreach (byte b in strBytes)
            {
                bufferPtr[offset++] = b;
            }
            bufferPtr[offset++] = 0; // null terminator
        }

        return offset;
    }

    /// <summary>
    /// Creates a FrameState from all currently networked Node3D objects in the scene.
    /// </summary>
    private FrameState CreateFrameStateFromCurrentObjects_Server(int frameIndex)
    {
        // get all the 3D objects in the scene we need to be sharing with clients
        var nodesToShare = _serverSceneNode.GetTree().GetNodesInGroup(NETWORKED_GROUP_NAME);
        // create a new FrameState array of SharedProperties, one for each node and copy the values we want into this new framestate
        FrameState newFrameState = new FrameState(nodesToShare.Count);
        newFrameState.FrameIndex = frameIndex;
        newFrameState.Node3DIDsDeletedForThisFrame = Node3DIDsDeletedThisFrame;

        int objectCount = 0;
        foreach (Node sharedObject in nodesToShare)
        {
            SharedProperties newSharedProperty = newFrameState.SharedObjects[objectCount++];

            newSharedProperty.ObjectIndex = IDToNetworkIDLookup.Find(sharedObject.GetInstanceId());

            // Determine if this is a 2D or 3D node and set properties accordingly
            if (sharedObject is Node3D node3D)
            {
                newSharedProperty.Position = node3D.GlobalPosition;
                newSharedProperty.Scale = node3D.Scale;
                newSharedProperty.Orientation = node3D.Rotation;
                newSharedProperty.ObjectIndex = IDToNetworkIDLookup.Find(sharedObject.GetInstanceId());

                // Get model radius from MeshInstance3D bounding box
                MeshInstance3D meshInstance = null;
                if (node3D is MeshInstance3D directMesh)
                {
                    meshInstance = directMesh;
                }
                else
                {
                    meshInstance = node3D.FindChild("*", true, false) as MeshInstance3D;
                }

                if (meshInstance != null)
                {
                    Aabb aabb = meshInstance.GetAabb();
                    Vector3 halfExtents = aabb.Size * 0.5f;
                    // Radius is the length from center to corner (bounding sphere)
                    newSharedProperty.ViewRadius = halfExtents.Length();
                }
                else
                {
                    newSharedProperty.ViewRadius = 1.0f; // Default fallback
                }

                if (node3D is CharacterBody3D characterBody)
                {
                    newSharedProperty.Velocity = characterBody.Velocity;
                }
                else if (node3D is RigidBody3D rigidBody)
                {
                    newSharedProperty.Velocity = rigidBody.LinearVelocity;
                }
                else
                {
                    newSharedProperty.Velocity = Vector3.Zero;
                }
            }
            else if (sharedObject is Node2D node2D)
            {
                newSharedProperty.ObjectIndex |= (short)SharedProperties.SharedObjectValueSetMask.kIs2DInMask;
                newSharedProperty.Position = new Vector3(node2D.GlobalPosition.X, node2D.GlobalPosition.Y, 0);
                newSharedProperty.Scale = new Vector3(node2D.Scale.X, node2D.Scale.Y, 1);
                newSharedProperty.Orientation = new Vector3(0, node2D.Rotation, 0);  // 2D rotation stored in Y

                newSharedProperty.ViewRadius = 1.0f; // Default for 2D

                // Handle 2D velocity
                if (node2D is CharacterBody2D characterBody2D)
                {
                    newSharedProperty.Velocity = new Vector3(characterBody2D.Velocity.X, characterBody2D.Velocity.Y, 0);
                }
                else if (node2D is RigidBody2D rigidBody2D)
                {
                    newSharedProperty.Velocity = new Vector3(rigidBody2D.LinearVelocity.X, rigidBody2D.LinearVelocity.Y, 0);
                }
                else
                {
                    newSharedProperty.Velocity = Vector3.Zero;
                }
            }
        }

        return newFrameState;
    }

    /// <summary>
    /// Writes SharedProperties from a FrameState to a buffer.
    /// If player is null, skips visibility culling and writes all objects.
    /// If lastAckedFrameState is null, writes full properties (no delta compression).
    /// Returns the number of bytes written and the object count.
    /// </summary>
    private unsafe (int bytesWritten, int objectCount) WriteSharedPropertiesToBuffer_Server(
        byte* bufferPtr,
        int startOffset,
        int maxBufferSize,
        FrameState frameState,
        FrameState lastAckedFrameState,
        NetworkingPlayerState player)
    {
        int currentOffset = startOffset;
        int objectsWrittenToBuffer = 0;

        foreach (SharedProperties sharedObject in frameState.SharedObjects)
        {
            SharedProperties oldSharedPropertyToCompareAgainst = null;

            // If player is provided, do visibility culling; otherwise send all objects
            bool shouldTransmitThisObject = true;
            if (player != null)
            {
                shouldTransmitThisObject = player.DetermineSharedObjectCanBeSeenByPlayer(
                    sharedObject.Position,
                    sharedObject.ViewRadius,
                    sharedObject.PlayingSound,
                    sharedObject.SoundRadius);
            }

            if (shouldTransmitThisObject)
            {
                // Find the corresponding entity in the last acked frame state for delta compression
                if (lastAckedFrameState != null)
                {
                    foreach (SharedProperties oldSharedProperties in lastAckedFrameState.SharedObjects)
                    {
                        if (oldSharedProperties.ObjectIndex == sharedObject.ObjectIndex)
                        {
                            oldSharedPropertyToCompareAgainst = oldSharedProperties;
                            break;
                        }
                    }
                }

                int offsetAdded = sharedObject.ConstructDataForObject(
                    bufferPtr + currentOffset,
                    maxBufferSize - currentOffset,
                    oldSharedPropertyToCompareAgainst);

                if (offsetAdded > 0)
                {
                    objectsWrittenToBuffer++;
                }
                if (offsetAdded == -1)
                {
                    Debug.Assert(false, "Buffer overflow while writing SharedProperties");
                }
                currentOffset += offsetAdded;
            }
        }

        return (currentOffset - startOffset, objectsWrittenToBuffer);
    }

    /// <summary>
    /// Writes a 3-byte integer to a buffer (little-endian).
    /// </summary>
    private unsafe void WriteInt24ToBuffer(byte* bufferPtr, int offset, int value)
    {
        bufferPtr[offset] = (byte)(value & 0xFF);
        bufferPtr[offset + 1] = (byte)((value >> 8) & 0xFF);
        bufferPtr[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    /// <summary>
    /// Reads a 3-byte integer from a buffer (little-endian).
    /// </summary>
    private unsafe int ReadInt24FromBuffer(byte* bufferPtr, int offset)
    {
        return bufferPtr[offset] | (bufferPtr[offset + 1] << 8) | (bufferPtr[offset + 2] << 16);
    }

    /// <summary>
    /// Processes a player input packet received from a client.
    /// Reads player index to route to correct PlayerState, then delegates parsing.
    /// </summary>
    public unsafe void ReadPlayerInputFromClient_Server(byte[] incomingBuffer, int bufferSize)
    {
        fixed (byte* bufferPtr = incomingBuffer)
        {
            int currentOffset = 0;

            // Read player index (1 byte)
            byte playerIndex = bufferPtr[currentOffset];
            currentOffset++;

            // Read input sequence number (4 bytes) - store for potential future use
            int inputSequenceNumber = *(int*)(bufferPtr + currentOffset);
            currentOffset += sizeof(int);

            // Validate player index
            if (playerIndex >= Players.Count)
            {
                // Invalid player index - ignore packet
                return;
            }

            // Get the player state and delegate remaining parsing
            NetworkingPlayerState player = Players[playerIndex];
            // note, we send multiple instances of player movements from the client to the server. If we've already got this one, don't process it again.
            if (player.inputSequenceNumber < inputSequenceNumber)
            {
                player.ReadSpecificPlayerInputFromClient(bufferPtr + currentOffset, bufferSize - currentOffset, inputSequenceNumber);
            }
        }
    }

    // on the server, construct packets to be sent to each player.
    public void TickNetwork_Server()
    {
        FrameState newFrameState = CreateFrameStateFromCurrentObjects_Server(FrameCount);
        Frames.Add(newFrameState);

        // now for each player, construct a buffer to be sent to them
        foreach(NetworkingPlayerState player in Players)
        {
            byte[] playerBuffer = player.CurrentUDPPlayerPacket;
            int currentOffset = 0;

            // Find the last acked framestate for delta compression
            FrameState lastAckedFrameState = null;
            if (player.LastAckedFrameClientReceived != -1)
            {
                foreach (FrameState olderFrameState in Frames)
                {
                    if (olderFrameState.FrameIndex == player.LastAckedFrameClientReceived)
                    {
                        lastAckedFrameState = olderFrameState;
                        break;
                    }
                }
            }

            // Write SharedProperties to buffer
            unsafe
            {
                fixed (byte* bufferPtr = playerBuffer)
                {
                    // Write frame index (3 bytes)
                    WriteInt24ToBuffer(bufferPtr, currentOffset, FrameCount);
                    currentOffset += 3;

                    // Skip object count for now, write it after we know how many objects
                    int objectCountOffset = currentOffset;
                    currentOffset += 2;

                    var (bytesWritten, objectCount) = WriteSharedPropertiesToBuffer_Server(
                        bufferPtr,
                        currentOffset,
                        NetworkingPlayerState.MAX_UDP_PACKET_SIZE,
                        newFrameState,
                        lastAckedFrameState,
                        player);

                    currentOffset += bytesWritten;

                    // Write object count at the reserved offset
                    *(short*)(bufferPtr + objectCountOffset) = (short)objectCount;

                    // Aggregate deleted node IDs since last acked frame
                    List<short> aggregatedNodeIDsDeleted = new List<short>();
                    foreach (FrameState olderFrameState in Frames)
                    {
                        if (olderFrameState.FrameIndex > player.LastAckedFrameClientReceived)
                        {
                            aggregatedNodeIDsDeleted.AddRange(olderFrameState.Node3DIDsDeletedForThisFrame);
                        }
                    }

                    // Write deleted node IDs to buffer
                    Debug.Assert(currentOffset + sizeof(short) <= NetworkingPlayerState.MAX_UDP_PACKET_SIZE, "Buffer overflow writing deleted node count");
                    *(short*)(bufferPtr + currentOffset) = (short)aggregatedNodeIDsDeleted.Count;
                    currentOffset += sizeof(short);

                    foreach (short nodeID in aggregatedNodeIDsDeleted)
                    {
                        Debug.Assert(currentOffset + sizeof(short) <= NetworkingPlayerState.MAX_UDP_PACKET_SIZE, "Buffer overflow writing deleted node ID");
                        *(short*)(bufferPtr + currentOffset) = nodeID;
                        currentOffset += sizeof(short);
                    }
                }
            }

            // transmit the packet to the player
        }

        // remove any old frame states we don't need any more, because all players have acked beyond them.
        RemoveOldUnusedFrameStatesForPlayer_Server();
        // clear out nodes that are deleted from this frame.
        Node3DIDsDeletedThisFrame = new List<short>();
        FrameCount++;
    }
    


    void RemoveOldUnusedFrameStatesForPlayer_Server()
    {
        if (Players.Count == 0)
        {
            return;
        }

        int lowestLastAckedFrame = int.MaxValue;
        foreach (NetworkingPlayerState player in Players)
        {
            if (player.LastAckedFrameClientReceived < lowestLastAckedFrame)
            {
                lowestLastAckedFrame = player.LastAckedFrameClientReceived;
            }
        }

        Frames.RemoveAll(frame => frame.FrameIndex < lowestLastAckedFrame);
    }

//******************************************************************************
//
//  Client side functions
//
//******************************************************************************

    public void Client_ResetNetworking()
    {
        // add a specific player for this player
        ClientPlayer = new NetworkingPlayerState();

        FramesFromServerCount = 0;
        IDToNetworkIDLookup = new HashedSlotArray();
    }

    /// <summary>
    /// Reads a list of null-terminated strings from a buffer.
    /// Format: [count: short][string1\0][string2\0]...
    /// Returns the new offset after reading.
    /// </summary>
    private unsafe int ReadStringListFromBuffer(byte* bufferPtr, int offset, int bufferSize, List<string> stringList)
    {
        stringList.Clear();

        // Read count as short
        Debug.Assert(offset + sizeof(short) <= bufferSize, "Buffer underflow reading string list count");
        short count = *(short*)(bufferPtr + offset);
        offset += sizeof(short);

        // Read each null-terminated string
        for (int i = 0; i < count; i++)
        {
            int stringStart = offset;
            // Find the null terminator
            while (offset < bufferSize && bufferPtr[offset] != 0)
            {
                offset++;
            }
            Debug.Assert(offset < bufferSize, "Buffer underflow reading string - no null terminator found");

            // Convert bytes to string
            int stringLength = offset - stringStart;
            byte[] stringBytes = new byte[stringLength];
            for (int j = 0; j < stringLength; j++)
            {
                stringBytes[j] = bufferPtr[stringStart + j];
            }
            stringList.Add(System.Text.Encoding.UTF8.GetString(stringBytes));

            offset++; // Skip the null terminator
        }

        return offset;
    }

    /// <summary>
    /// Reads SharedProperties from a buffer and creates/updates Node3D objects.
    /// Shared between initial TCP packet and per-frame UDP packet processing.
    /// Returns the number of bytes read.
    /// </summary>
    private unsafe int ReadSharedPropertiesFromBuffer_Client(byte* bufferPtr, int startOffset, int bufferSize, short objectCount)
    {
        int currentOffset = startOffset;

        for (int i = 0; i < objectCount; i++)
        {
            // Read the encoded object index from the buffer
            short encodedObjectIndex = *(short*)(bufferPtr + currentOffset);
            byte baseMask = bufferPtr[currentOffset + 2];

            // Decode the object index and extract extended flags into the full mask
            SharedProperties.DecodeObjectIndexAndMask(encodedObjectIndex, baseMask, out short objectIndex, out short fullMask);

            // Check if this is a 2D object
            bool is2D = (fullMask & 0x100) != 0;  // kIs2D flag

            // Find or create the node for this object
            Node targetNode;
            if (IDToNetworkIDLookup.IsOccupied(objectIndex))
            {
                // Existing object - get it from the lookup
                ulong instanceId = IDToNetworkIDLookup.GetAt(objectIndex);
                targetNode = GodotObject.InstanceFromId(instanceId) as Node;
            }
            else
            {
                // New object - create appropriate node type and register it at the same index as server
                if (is2D)
                {
                    targetNode = new Node2D();
                }
                else
                {
                    targetNode = new Node3D();
                }
                // Add to client scene tree
                _clientSceneNode.AddChild(targetNode);
                IDToNetworkIDLookup.InsertAt(objectIndex, targetNode.GetInstanceId());
            }

            // Read and apply the SharedProperty data to the node
            int bytesRead = SharedProperties.ReadDataForObject(
                bufferPtr + currentOffset,
                targetNode,
                fullMask
            );

            currentOffset += bytesRead;
        }

        return currentOffset - startOffset;
    }

    /// <summary>
    /// Processes deleted node IDs from a buffer and removes the corresponding Node3D objects.
    /// Returns the number of bytes read.
    /// </summary>
    private unsafe int ProcessDeletedNodesFromBuffer_Client(byte* bufferPtr, int startOffset, int bufferSize)
    {
        int currentOffset = startOffset;

        // Read deleted nodes count
        Debug.Assert(currentOffset + sizeof(short) <= bufferSize, "Buffer underflow reading deleted node count");
        short deletedCount = *(short*)(bufferPtr + currentOffset);
        currentOffset += sizeof(short);

        // Process deleted node IDs
        for (int i = 0; i < deletedCount; i++)
        {
            Debug.Assert(currentOffset + sizeof(short) <= bufferSize, "Buffer underflow reading deleted node ID");
            short deletedNodeID = *(short*)(bufferPtr + currentOffset);
            currentOffset += sizeof(short);

            // Look up the instance ID from the network ID
            if (IDToNetworkIDLookup.IsOccupied(deletedNodeID))
            {
                ulong instanceId = IDToNetworkIDLookup.GetAt(deletedNodeID);
                Node3D nodeToDelete = GodotObject.InstanceFromId(instanceId) as Node3D;
                if (nodeToDelete != null)
                {
                    nodeToDelete.QueueFree();
                }
                IDToNetworkIDLookup.RemoveAt(deletedNodeID);
            }
        }

        return currentOffset - startOffset;
    }

    /// <summary>
    /// Processes the initial TCP game packet from the server.
    /// Reads game-specific data, precache lists, and all initial SharedProperties.
    /// </summary>
    public void NewGamePacketReceived_Client(byte[] incomingBuffer)
    {
        int currentOffset = 0;

        unsafe
        {
            fixed (byte* bufferPtr = incomingBuffer)
            {
                // First, call the game-specific data reader callback if registered
                if (gameSpecificDataReaderCallback != null)
                {
                    int gameSpecificBytesRead = gameSpecificDataReaderCallback(bufferPtr, incomingBuffer.Length);
                    currentOffset += gameSpecificBytesRead;
                }

                // Read player index (1 byte) - which player we are on the server
                Debug.Assert(currentOffset + 1 <= incomingBuffer.Length, "Buffer underflow reading player index");
                byte playerIndex = bufferPtr[currentOffset];
                currentOffset++;

                // Store the player index on our local player state
                ClientPlayer.WhichPlayerAreWeOnServer = playerIndex;

                // Read SoundsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, SoundsUsed);

                // Read ModelsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, ModelsUsed);

                // Read AnimationsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, AnimationsUsed);

                // Read ParticleEffectsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, ParticleEffectsUsed);

                // Read frame index (3 bytes)
                Debug.Assert(currentOffset + 3 <= incomingBuffer.Length, "Buffer underflow reading initial frame index");
                int frameIndex = ReadInt24FromBuffer(bufferPtr, currentOffset);
                currentOffset += 3;

                // Store the frame index as the last acked frame for this client
                ClientPlayer.LastAckedFrameClientReceived = frameIndex;

                // Read object count
                Debug.Assert(currentOffset + sizeof(short) <= incomingBuffer.Length, "Buffer underflow reading initial object count");
                short objectCount = *(short*)(bufferPtr + currentOffset);
                currentOffset += sizeof(short);

                // Read all SharedProperties and create Node3D objects
                int bytesRead = ReadSharedPropertiesFromBuffer_Client(
                    bufferPtr,
                    currentOffset,
                    incomingBuffer.Length,
                    objectCount
                );
                currentOffset += bytesRead;
            }
        }

        FramesFromServerCount = 1; // We've received the initial state
    }

    /// <summary>
    /// Processes a per-frame UDP packet from the server.
    /// Reads frame index, SharedProperties updates, and deleted node IDs.
    /// </summary>
    public void FramePacketReceived_Client(byte[] incomingBuffer)
    {
        int currentOffset = 0;

        unsafe
        {
            fixed (byte* bufferPtr = incomingBuffer)
            {
                // Read frame index (3 bytes)
                int frameIndex = ReadInt24FromBuffer(bufferPtr, currentOffset);
                currentOffset += 3;

                // Store the frame index as the last acked frame for this client
                ClientPlayer.LastAckedFrameClientReceived = frameIndex;

                // Read object count (2 bytes)
                short objectCount = *(short*)(bufferPtr + currentOffset);
                currentOffset += sizeof(short);

                // Read SharedProperties using shared function
                int bytesRead = ReadSharedPropertiesFromBuffer_Client(
                    bufferPtr,
                    currentOffset,
                    incomingBuffer.Length,
                    objectCount
                );
                currentOffset += bytesRead;

                // Process deleted nodes using shared function
                ProcessDeletedNodesFromBuffer_Client(bufferPtr, currentOffset, incomingBuffer.Length);
            }
        }

        FramesFromServerCount++;
    }
}

public class HashedSlotArray
{
    private const int ARRAY_SIZE = 16384;
    private const int INDEX_MASK = 0x3FFF; // For fast modulo via bitwise AND (14 bits, top 2 reserved for flags)

    private ulong[] slots = new ulong[ARRAY_SIZE];
    private bool[] occupied = new bool[ARRAY_SIZE]; // Track which slots are in use

    /// <summary>
    /// Inserts a ulong value using the value itself as a hash for initial placement.
    /// Returns the index where stored, or -1 if array is full.
    /// </summary>
    public short Insert(ulong value)
    {
        // Hash: XOR all 16-bit chunks together for better distribution
        short startIndex = (short)((value ^ (value >> 16) ^ (value >> 32) ^ (value >> 48)) & INDEX_MASK);
        short currentIndex = startIndex;

        do
        {
            if (!occupied[currentIndex])
            {
                slots[currentIndex] = value;
                occupied[currentIndex] = true;
                return currentIndex;
            }

            // Linear probe with wraparound
            currentIndex = (short)((currentIndex + 1) & INDEX_MASK);
        }
        while (currentIndex != startIndex);

        // Array is full
        return -1;
    }

    /// <summary>
    /// Removes the value at the given index.
    /// </summary>
    public void RemoveAt(short index)
    {
        occupied[index] = false;
    }

    /// <summary>
    /// Gets the value at the given index.
    /// </summary>
    public ulong GetAt(short index)
    {
        return slots[index];
    }

    /// <summary>
    /// Inserts a ulong value at a specific index.
    /// Used on the client side to match server-assigned indices.
    /// </summary>
    public void InsertAt(short index, ulong value)
    {
        slots[index] = value;
        occupied[index] = true;
    }

    public bool IsOccupied(short index)
    {
        return occupied[index];
    }

    /// <summary>
    /// Finds the index of a given ulong value in the array.
    /// Returns the index where found, or -1 if not present.
    /// </summary>
    public short Find(ulong value)
    {
        // Hash: XOR all 16-bit chunks together for better distribution
        short startIndex = (short)((value ^ (value >> 16) ^ (value >> 32) ^ (value >> 48)) & INDEX_MASK);
        short currentIndex = startIndex;

        do
        {
            if (occupied[currentIndex] && slots[currentIndex] == value)
            {
                return currentIndex;
            }

            // If we hit an unoccupied slot, the value isn't in the array
            if (!occupied[currentIndex])
            {
                return -1;
            }

            // Linear probe with wraparound
            currentIndex = (short)((currentIndex + 1) & INDEX_MASK);
        }
        while (currentIndex != startIndex);

        // Wrapped all the way around, not found
        return -1;
    }
}
