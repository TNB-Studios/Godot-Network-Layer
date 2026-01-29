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
    public List<PlayerState> Players { get; set; } = new List<PlayerState>();
    public List<FrameState> Frames { get; set; } = new List<FrameState>();
    // NOTE - these need to be precached BEFORE we start the game; they cannot be changed post game start, since they are transmitted
    // to all clients on each Client being initialized. Also, they can't ba larger than 65536 in size, but that should be enough for anyone - this is the max index size in individual SharedProperties
    public List<string> SoundsUsed { get; set; } = new List<string>();
    public List<string> ModelsUsed { get; set; } = new List<string>();
    public List<string> AnimationsUsed { get; set; } = new List<string>();

    private int FrameCount = 0;

    public NetworkManager()
    {
    }

    // on the server, construct packets to be sent to each player.
    public void TickNetwork_Server()
    {
        // get all the 3D objects in the scene we need to be sharing with clients
        Array<XmlNode3D> nodesToShare = GetTree().GetNodesInGroup("share");
        // create a new FrameState array of SharedProperites, one for each node and copy the values we want in to this new framestate
        FrameState newFrameState = new FrameState(nodesToShare.Count);
        newFrameState.FrameIndex = FrameCount;

        int objectCount = 0;
        foreach (Node3D sharedObject in nodesToShare)
        {
            SharedProperties newSharedProperty = newFrameState.SharedObjects[objectCount++];
            newSharedProperty.Position = sharedObject.GlobalPosition;
            newSharedProperty.Scale = sharedObject.Scale;
            newSharedProperty.Orientation = sharedObject.Rotation;
            newSharedProperty.OriginatingObjectID = sharedObject.GetInstanceId();

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
                newSharedProperty.ModelRadius = halfExtents.Length();
            }
            else
            {
                newSharedProperty.ModelRadius = 1.0f; // Default fallback
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
                bool shouldTransmit = player.DetermineSharedObjectCanBeSeenByPlayer(sharedProperties.Position, sharedProperties.ModelRadius, sharedProperties.PlayingSound, sharedProperties.SoundRadius);
                
                if (shouldTransmit)
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
        }

        // transmit the packet.

        // remove any old frame states we don't need any more, because all players have acked beyond them.
        RemoveOldUnusedFrameStatesForPlayer();   
        FrameCount++;
    }
    
    public void TickNetwork_Client()
    {
        RemoveOldUnusedFrameStatesForPlayer();   
        FrameCount++;
    }

    void RemoveOldUnusedFrameStatesForPlayer()
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
}
