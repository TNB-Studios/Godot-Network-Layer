using Godot;
using System.Collections.Generic;
using System.Diagnostics;

public class NetworkManager_Server : NetworkManager_Common
{
	private int FrameCount = 0;
	public List<FrameState> Frames { get; set; }
	private List<short> Node3DIDsDeletedThisFrame;
	public List<NetworkingPlayerState> Players { get; set; }  // note, on the server, this will have mulitple entries.



	public unsafe delegate int GameSpecificDataWriter(byte* bufferPtr, int bufferSize);
	private GameSpecificDataWriter gameSpecificDataWriterCallback = null;

	private bool gameStarted = false;

	private const int TCP_INIT_PACKET_SIZE = 65536;

	public NetworkManager_Server(Node rootSceneNode) : base(rootSceneNode)
	{

	}

    public void AddNode3DToNetworkedNodeList(Node3D newNode)
    {
        newNode.AddToGroup(NETWORKED_GROUP_NAME);
        IDToNetworkIDLookup.Insert(newNode.GetInstanceId());
    }

    public void RemoveNode3DfromNetworkNodeList(Node3D nodeToRemove)
    {
        short indexOfObjectBeingRemoved = IDToNetworkIDLookup.Find(nodeToRemove.GetInstanceId());
        Node3DIDsDeletedThisFrame.Add(indexOfObjectBeingRemoved);
        IDToNetworkIDLookup.RemoveAt(indexOfObjectBeingRemoved);
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

public void InitNewGame_Server()
	{
		// zeroed by default.
		IDToNetworkIDLookup = new HashedSlotArray();
	}

	public void NewGameSetup_Server(int playerCount, int playerOnServer = -1)
	{
		// note, not reseting the arrays of animations, models and sounds used, since this shouldn't change game to game.
		Players = new List<NetworkingPlayerState>();
		Frames = new List<FrameState>();

		// Create a PlayerState for each player
		for (int i = 0; i < playerCount; i++)
		{
			NetworkingPlayerState newPlayer = new NetworkingPlayerState();
			newPlayer.WhichPlayerAreWeOnServer = i;
			if (playerOnServer == i)
			{
				newPlayer.IsOnServer = true;
			}
			Players.Add(newPlayer);
		}

		if (playerOnServer != -1 && playerOnServer < Players.Count)
		{
			Players[playerOnServer].IsOnServer = true;
		}

		Node3DIDsDeletedThisFrame = new List<short>();

		FrameCount = 0;

		gameStarted = true;

		// Create the TCP initialization packet with all precache data and initial object states
		byte[] tcpInitPacket = new byte[TCP_INIT_PACKET_SIZE];
		int currentOffset = 0;
		int playerIndexOffset = 0; // Will store the offset of the player index byte

		for (int i = 0; i <TCP_INIT_PACKET_SIZE; i++)
		{
			tcpInitPacket[i] = 255;
		}

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
				WriteInt24ToBuffer(bufferPtr, currentOffset, FrameCount);
				currentOffset += 3;

				// Write object count
				Debug.Assert(currentOffset + sizeof(short) <= TCP_INIT_PACKET_SIZE, "Buffer overflow writing initial object count");
				*(short*)(bufferPtr + currentOffset) = (short)initialFrameState.SharedObjects.Count;
				currentOffset += sizeof(short);

				// Write all SharedProperties to the buffer (no player culling, no delta compression)
				(int bytesWritten, int objectCount) = WriteSharedPropertiesToBuffer_Server(
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
	/// If player is on same server, calls client receive directly.
	/// </summary>
	protected void SendTCPInitPacketToPlayer_Server(byte[] packet, int packetSize, int playerIndex)
	{
		NetworkingPlayerState player = Players[playerIndex];

		if (player.IsOnServer)
		{
			// Player is local - call client receive directly
			Globals.worldManager_client.networkManager_client.NewGamePacketReceived_Client(packet, packetSize);
		}
		else
		{
			// TODO: Implement actual TCP transmission to the remote player
		}
	}

	/// <summary>
	/// Writes a list of strings to a buffer with count prefix and null terminators.
	/// Format: [count: short][string1\0][string2\0]...
	/// Returns the new offset after writing.
	/// </summary>
	protected unsafe int WriteStringListToBuffer(byte* bufferPtr, int offset, int bufferSize, List<string> stringList)
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
	protected FrameState CreateFrameStateFromCurrentObjects_Server(int frameIndex)
	{
		// get all the 3D objects in the scene we need to be sharing with clients
		Godot.Collections.Array<Node> nodesToShare = _rootSceneNode.GetTree().GetNodesInGroup(NETWORKED_GROUP_NAME);
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

				// Find attached model by checking children for scene file paths
				newSharedProperty.CurrentModel = -1;
				foreach (Node child in node3D.GetChildren())
				{
					string scenePath = child.SceneFilePath;
					if (!string.IsNullOrEmpty(scenePath))
					{
						// Remove "res://" prefix if present for comparison
						string relativePath = scenePath.StartsWith("res://") ? scenePath.Substring(6) : scenePath;
						int modelIndex = ModelsUsed.IndexOf(relativePath);
						if (modelIndex >= 0)
						{
							newSharedProperty.CurrentModel = (short)modelIndex;
							break;
						}
					}
				}

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
	protected unsafe (int bytesWritten, int objectCount) WriteSharedPropertiesToBuffer_Server(
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

					(int bytesWritten, int objectCount) = WriteSharedPropertiesToBuffer_Server(
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

					// Store the actual packet size
					player.CurrentUDPPlayerPacketSize = currentOffset;
				}
			}

			// transmit the packet to the player
			if (player.IsOnServer)
			{
				// Player is local - call client receive directly
				Globals.worldManager_client.networkManager_client.FramePacketReceived_Client(player.CurrentUDPPlayerPacket, player.CurrentUDPPlayerPacketSize);
			}
			else
			{
				// TODO: Implement actual UDP transmission to the remote player
				player.TransmitUDPFromServerToClient();
			}
		}

		// remove any old frame states we don't need any more, because all players have acked beyond them.
		RemoveOldUnusedFrameStatesForPlayer_Server();
		// clear out nodes that are deleted from this frame.
		Node3DIDsDeletedThisFrame = new List<short>();
		FrameCount++;
	}



	protected void RemoveOldUnusedFrameStatesForPlayer_Server()
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


}
