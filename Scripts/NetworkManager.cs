using System.ComponentModel;
using System.Data;
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
    // common to both server and client
    public List<NetworkingPlayerState> Players { get; set; }  // note, on the server, this will have mulitple entries. On the Client, only one
 
    // NOTE - On the server these need to be precached BEFORE we start the game; they cannot be changed post game start, since they are transmitted
    // to all clients on each Client being initialized. Also, they can't ba larger than 65536 in size, but that should be enough for anyone - this is the max index size in individual SharedProperties
    private List<string> SoundsUsed { get; set; } = new List<string>();
    private List<string> ModelsUsed { get; set; } = new List<string>();
    private List<string> AnimationsUsed { get; set; } = new List<string>();
    private List<string> ParticleEffectsUsed {get; set;} = new List<string>();


    private HashedSlotArray IDToNetworkIDLookup;

    // Client Side specific
    private int FramesFromServerCount = 0;

    // Server Side Specific
    private int FrameCount = 0;
    public List<FrameState> Frames { get; set; } 
    private List<short> Node3DIDsDeletedThisFrame;

    private static readonly string NETWORKED_GROUP_NAME = "networked";

    private bool gameStarted = false;

    // Callback delegate for game-specific data to be written to TCP init packet (server-side)
    public unsafe delegate int GameSpecificDataWriter(byte* bufferPtr, int bufferSize);
    private GameSpecificDataWriter gameSpecificDataWriterCallback = null;

    // Callback delegate for game-specific data to be read from TCP init packet (client-side)
    public unsafe delegate int GameSpecificDataReader(byte* bufferPtr, int bufferSize);
    private GameSpecificDataReader gameSpecificDataReaderCallback = null;

    public NetworkManager()
    {
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

    public void AddPrecaheModelsUsed_Server(string modelName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Models used at this point!!");   
        ModelsUsed.Add(modelName);
    }

    public void AddPrecaheSoundsUsed_Server(string soundsName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Sounds used at this point!!");
        SoundsUsed.Add(soundsName);
    }

    public void AddPrecaheParticleEffectsUsed_Server(string soundsName)
    {
        Debug.Assert(!gameStarted,"Game Started. Can't be adding Particle Effects used at this point!!");
        SoundsUsed.Add(soundsName);
    }

    public void AddNode3DToNetworkedNodeList_Server(Node3D newNode)
    {
        node.AddToGroup(NETWORKED_GROUP_NAME);
        IDToNetworkIDLookup.Insert(newNode.GetInstanceId());
    }

    public void RemoveNode3DfromNetworkNodeList_Server(Node3D nodeToRemove)
    {
        short indexOfObjectBeingRemoved = IDToNetworkIDLookup.Find(nodeToRemove.GetInstanceId());
        Node3DIDsDeletedThisFrame.Add(indexOfObjectBeingRemoved);
    }

    private const int TCP_INIT_PACKET_SIZE = 65536;

    public byte[] NewGameSetup_Server()
    {
        // note, not reseting the arrays of animations, models and sounds used, since this shouldn't change game to game.
        Players = new List<PlayerState>();
        Frames = new List<FrameState>();

        Node3DIDsDeletedThisFrame = new List<short>();

        FrameCount = 0;

        // zeroed by default.
        IDToNetworkIDLookup = new HashedSlotArray();

        gameStarted = true;

        // Create the TCP initialization packet with all precache data and initial object states
        byte[] tcpInitPacket = new byte[TCP_INIT_PACKET_SIZE];
        int currentOffset = 0;

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

                // Write object count
                Debug.Assert(currentOffset + sizeof(short) <= TCP_INIT_PACKET_SIZE, "Buffer overflow writing initial object count");
                *(short*)(bufferPtr + currentOffset) = (short)initialFrameState.SharedObjects.Length;
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
            }
        }

        return tcpInitPacket;
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
        Array<Node3D> nodesToShare = GetTree().GetNodesInGroup(NETWORKED_GROUP_NAME);
        // create a new FrameState array of SharedProperties, one for each node and copy the values we want into this new framestate
        FrameState newFrameState = new FrameState(nodesToShare.Count);
        newFrameState.FrameIndex = frameIndex;
        newFrameState.Node3DIDsDeletedForThisFrame = Node3DIDsDeletedThisFrame;

        int objectCount = 0;
        foreach (Node3D sharedObject in nodesToShare)
        {
            SharedProperties newSharedProperty = newFrameState.SharedObjects[objectCount++];
            newSharedProperty.Position = sharedObject.GlobalPosition;
            newSharedProperty.Scale = sharedObject.Scale;
            newSharedProperty.Orientation = sharedObject.Rotation;
            newSharedProperty.OriginatingObjectID = IDToNetworkIDLookup.Find(sharedObject.GetInstanceId());

            // Get model radius from MeshInstance3D bounding box
            MeshInstance3D meshInstance = null;
            if (sharedObject is MeshInstance3D directMesh)
            {
                meshInstance = directMesh;
            }
            else
            {
                meshInstance = sharedObject.FindChild("*", true, false) as MeshInstance3D;
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

            if (sharedObject is CharacterBody3D characterBody)
            {
                newSharedProperty.Velocity = characterBody.Velocity;
            }
            else if (sharedObject is RigidBody3D rigidBody)
            {
                newSharedProperty.Velocity = rigidBody.LinearVelocity;
            }
            else
            {
                newSharedProperty.Velocity = Vector3.Zero;
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
        PlayerState player)
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
                        if (oldSharedProperties.OriginatingObjectID == sharedObject.OriginatingObjectID)
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

    // on the server, construct packets to be sent to each player.
    public void TickNetwork_Server()
    {
        FrameState newFrameState = CreateFrameStateFromCurrentObjects_Server(FrameCount);
        Frames.Add(newFrameState);

        // now for each player, construct a buffer to be sent to them
        foreach(PlayerState player in Players)
        {
            byte[] playerBuffer = player.CurrentUDPPlayerPacket;
            int currentOffset = 4; // first 2 bytes are frame count, next 2 are object count

            // Find the last acked framestate for delta compression
            FrameState lastAckedFrameState = null;
            if (player.LastAckedFrame != -1)
            {
                foreach (FrameState olderFrameState in Frames)
                {
                    if (olderFrameState.FrameIndex == player.LastAckedFrame)
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
                    var (bytesWritten, objectCount) = WriteSharedPropertiesToBuffer_Server(
                        bufferPtr,
                        currentOffset,
                        NetworkingPlayerState.MAX_UDP_PACKET_SIZE,
                        newFrameState,
                        lastAckedFrameState,
                        player);

                    currentOffset += bytesWritten;

                    // Write object count at offset 2
                    *(short*)(bufferPtr + 2) = (short)objectCount;

                    // Aggregate deleted node IDs since last acked frame
                    List<short> aggregatedNodeIDsDeleted = new List<short>();
                    foreach (FrameState olderFrameState in Frames)
                    {
                        if (olderFrameState.FrameIndex > player.LastAckedFrame)
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
        foreach (PlayerState player in Players)
        {
            if (player.LastAckedFrame < lowestLastAckedFrame)
            {
                lowestLastAckedFrame = player.LastAckedFrame;
            }
        }

        Frames.RemoveAll(frame => frame.FrameSet < lowestLastAckedFrame);
    }

//******************************************************************************
//
//  Client side functions
//
//******************************************************************************

    public NetworkingPlayerState Client_ResetNetworking()
    {
        Players = new List<NetworkingPlayerState>();
        // add a specific player for this player
        NetworkingPlayerState thisPlayer = new NetworkingPlayerState();
        Players.Add(thisPlayer);

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
            // Read the object index from the buffer
            short objectIndex = *(short*)(bufferPtr + currentOffset);

            // Find or create the Node3D for this object
            Node3D targetNode;
            if (IDToNetworkIDLookup.IsOccupied(objectIndex))
            {
                // Existing object - get it from the lookup
                ulong instanceId = IDToNetworkIDLookup.GetAt(objectIndex);
                targetNode = GodotObject.InstanceFromId(instanceId) as Node3D;
            }
            else
            {
                // New object - create a Node3D and register it at the same index as server
                targetNode = new Node3D();
                // TODO: Add to scene tree
                IDToNetworkIDLookup.InsertAt(objectIndex, targetNode.GetInstanceId());
            }

            // Read and apply the SharedProperty data to the Node3D
            int bytesRead = SharedProperties.ReadDataForObject(
                bufferPtr + currentOffset,
                targetNode
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

                // Read SoundsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, SoundsUsed);

                // Read ModelsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, ModelsUsed);

                // Read AnimationsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, AnimationsUsed);

                // Read ParticleEffectsUsed list
                currentOffset = ReadStringListFromBuffer(bufferPtr, currentOffset, incomingBuffer.Length, ParticleEffectsUsed);

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
                // Read frame index (2 bytes)
                short frameIndex = *(short*)(bufferPtr + currentOffset);
                currentOffset += sizeof(short);

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
    private const int ARRAY_SIZE = 65536;
    private const int INDEX_MASK = 0xFFFF; // For fast modulo via bitwise AND

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
