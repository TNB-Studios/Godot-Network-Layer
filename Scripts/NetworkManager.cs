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

    public NetworkManager()
    {
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

    public void NewGameSetup_Server()
    {
        // note, not reseting the arrays of animations, models and sounds used, since this shouldn't change game to game.
        Players = new List<PlayerState>();
        Frames = new List<FrameState>();

        Node3DIDsDeletedThisFrame = new List<short>();

        FrameCount = 0;

        // zeroed by default.
        IDToNetworkIDLookup = new Array<ulong>(65536);

        gameStarted = true;
    }

    // on the server, construct packets to be sent to each player.
    public void TickNetwork_Server()
    {
        // get all the 3D objects in the scene we need to be sharing with clients
        Array<XmlNode3D> nodesToShare = GetTree().GetNodesInGroup(NETWORKED_GROUP_NAME);
        // create a new FrameState array of SharedProperites, one for each node and copy the values we want in to this new framestate
        FrameState newFrameState = new FrameState(nodesToShare.Count);
        newFrameState.FrameIndex = FrameCount;
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
        Frames.Add(newFrameState);

        // now for each player, construct a buffer to be sent to them. While they may all look the same now, they won't when we introduce frustrum culling per player
        foreach(PlayerState player in Players)
        {
            Byte[] playerBuffer = player.CurrentPlayerPacket;
            int currentOffset = 4;
            short* objectCountPtr = null;
            // actual buffer starts 4 bytes in - first 2 bytes are the frame count, and the next two are the count of sharedProperty objects
            unsafe
            {
                fixed (byte* bufferPtr = playerBuffer)
                {
                    objectCountPtr = (short*)(bufferPtr + 2);
                }
            }
            int objectsWrittenToBuffer = 0;
            FrameState lastAckedFrameState = null;

            // Now find the last acked framestate.
            if (player.LastAckedFrame != -1)
            {
                foreach(FrameState olderFrameState in Frames)
                {
                    if (olderFrameState.FrameIndex == player.LastAckedFrame)
                    {
                        lastAckedFrameState = olderFrameState;
                        break;
                    }
                }
            }

            foreach(SharedProperties sharedObject in newFrameState)
            {
                SharedProperties oldSharedPropertyToCompareAgainst = null;

                // use the player position, orientation and view frustum to determine if this object can actually be seen.
                // note, if the object has a sound on it, transmit it anyway, so we have a correct 3D position for the sound to move.
                bool shouldTransmitThisObject = player.DetermineSharedObjectCanBeSeenByPlayer(sharedProperties.Position, sharedProperties.ViewRadius, sharedProperties.PlayingSound, sharedProperties.SoundRadius);
                
                if (shouldTransmitThisObject)
                {
                    // now find the corresponding entity in the last acked frame state, assuming either exists (and it's possible they won't, if it's a new object or first frame)
                    if (lastAckedFrameState != null)
                    {
                        // note, can probably make this a little faster, searching from the last found position onwards, if we really need to.
                        foreach (SharedProperties oldSharedProperties in lastAckedFrameState.SharedObjects)
                        {
                            if (oldSharedProperties.OriginatingObjectID == sharedObject.OriginatingObjectID)
                            {
                                oldSharedPropertyToCompareAgainst = oldSharedProperties;
                                break;
                            }
                        }
                    }

                    int offsetAdded = sharedObject.ConstructDataForObject(playerBuffer[currentOffset], PlayerState.MAX_PACKET_SIZE - currentOffset, oldSharedPropertyToCompareAgainst);
                    if (offsetAdded > 0)
                    {
                        objectsWrittenToBuffer++;
                    }
                    if (offsetAdded == -1)
                    {
                        // oh shit. We blew the network packet. What do we do here?
                        Assert(false);
                    }
                    currentOffset += offsetAdded;
                }
            }
            objectCountPtr = objectsWrittenToBuffer;

            // now add to the buffer all the node ID's that are deleted since the last frame state the player acked, so the client can know to delete them.
            List<short> aggregatedNodeIDsDeleted = new List<short>();
            foreach(FrameState olderFrameState in Frames)
            {
                if (olderFrameState.FrameIndex > player.LastAckedFrame)
                {
                    aggregatedNodeIDsDeleted.AddRange(olderFrameState.Node3DIDsDeletedForThisFrame);
                    break;
                }
            }
            // now add them into the buffer.
            unsafe
            {
                fixed (byte* bufferPtr = playerBuffer)
                {
                    // Write the count as a short
                    *(short*)(bufferPtr + currentOffset) = (short)aggregatedNodeIDsDeleted.Count;
                    currentOffset += sizeof(short);

                    // Write each deleted node ID
                    foreach (short nodeID in aggregatedNodeIDsDeleted)
                    {
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
        Players = new List<PlayerState>();
        // add a specific player for this player
        NetworkingPlayerState thisPlayer = new NetworkingPlayerState();
        Players.Add(thisPlayer);

        FramesFromServerCount = 0;
        IDToNetworkIDLookup = new Array<ulong>(65536);
    }

    public void PacketReceived_Client(byte[] incomingBuffer)
    {
        int currentOffset = 0;

        unsafe
        {
            fixed (byte* bufferPtr = incomingBuffer)
            {
                // Read frame index (2 bytes)
                short frameIndex = *(short*)(bufferPtr + currentOffset);
                currentOffset += 2;

                // Read object count (2 bytes)
                short objectCount = *(short*)(bufferPtr + currentOffset);
                currentOffset += 2;

                // Process each SharedProperty
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

                // Read deleted nodes count
                short deletedCount = *(short*)(bufferPtr + currentOffset);
                currentOffset += 2;

                // Process deleted node IDs
                for (int i = 0; i < deletedCount; i++)
                {
                    short deletedNodeID = *(short*)(bufferPtr + currentOffset);
                    currentOffset += 2;

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
