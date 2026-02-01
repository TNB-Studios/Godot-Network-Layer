using Godot;
using System.Collections.Generic;
using System.Diagnostics;

public class NetworkManager_Client : NetworkManager_Common
{
	private int FramesFromServerCount = 0;
	public NetworkingPlayerState ClientPlayer;

	public unsafe delegate int GameSpecificDataReader(byte* bufferPtr, int bufferSize);
	private GameSpecificDataReader gameSpecificDataReaderCallback = null;

	public delegate void AddNodeToClientScene(Node node);
	private AddNodeToClientScene addNodeToClientSceneCallback = null;

	// Callback delegate for updating a node's model (for multi-pass rendering)
	public delegate void UpdateNodeModel(Node node, short modelIndex);
	private UpdateNodeModel updateNodeModelCallback = null;

	// Callback delegate for syncing node properties after all updates (for multi-pass rendering)
	public delegate void SyncNodeProperties(Node node);
	private SyncNodeProperties syncNodePropertiesCallback = null;

	// Callback delegate for deleting a node (for multi-pass rendering)
	public delegate void DeleteNodeFromClientScene(Node node);
	private DeleteNodeFromClientScene deleteNodeFromClientSceneCallback = null;

	public NetworkManager_Client(Node rootSceneNode) : base(rootSceneNode)
	{

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

	/// <summary>
	/// Registers a callback to add a new node to the client scene.
	/// Used for multi-pass rendering where nodes need to be added to multiple viewports.
	/// Client-side only.
	/// </summary>
	public void RegisterAddNodeToClientSceneCallback(AddNodeToClientScene callback)
	{
		addNodeToClientSceneCallback = callback;
	}

	/// <summary>
	/// Registers a callback to update a node's model.
	/// Used for multi-pass rendering where model changes need to be applied to multiple viewports.
	/// Client-side only.
	/// </summary>
	public void RegisterUpdateNodeModelCallback(UpdateNodeModel callback)
	{
		updateNodeModelCallback = callback;
	}

	/// <summary>
	/// Registers a callback to sync node properties (position, rotation, scale, animation, particles) to other viewports.
	/// Called at the end of ReadDataForObject after all properties are applied.
	/// Client-side only.
	/// </summary>
	public void RegisterSyncNodePropertiesCallback(SyncNodeProperties callback)
	{
		syncNodePropertiesCallback = callback;
	}

	/// <summary>
	/// Registers a callback to delete a node from the client scene.
	/// Used for multi-pass rendering where nodes need to be removed from multiple viewports.
	/// Client-side only.
	/// </summary>
	public void RegisterDeleteNodeFromClientSceneCallback(DeleteNodeFromClientScene callback)
	{
		deleteNodeFromClientSceneCallback = callback;
	}

	/// <summary>
	/// Called by SharedProperties when a model needs to be updated on a node.
	/// Routes through the callback if registered, otherwise handles directly.
	/// </summary>
	public void ApplyModelToNode(Node targetNode, short modelIndex)
	{
		if (updateNodeModelCallback != null)
		{
			updateNodeModelCallback(targetNode, modelIndex);
		}
		else
		{
			// Default behavior - apply model directly to the node
			ApplyModelToNodeDirect(targetNode, modelIndex);
		}
	}

	/// <summary>
	/// Directly applies a model to a node without going through the callback.
	/// Used as fallback when no callback is registered.
	/// </summary>
	public void ApplyModelToNodeDirect(Node targetNode, short modelIndex)
	{
		if (modelIndex >= 0 && modelIndex < ModelsUsed.Count)
		{
			string modelPath = "res://" + ModelsUsed[modelIndex];

			// Check if we already have this model attached
			bool modelAlreadyAttached = false;
			foreach (Node child in targetNode.GetChildren())
			{
				if (child.SceneFilePath == modelPath)
				{
					modelAlreadyAttached = true;
					break;
				}
			}

			if (!modelAlreadyAttached)
			{
				// Remove any existing model children first
				foreach (Node child in targetNode.GetChildren())
				{
					if (!string.IsNullOrEmpty(child.SceneFilePath))
					{
						child.QueueFree();
					}
				}

				// Load and attach the new model
				PackedScene modelScene = GD.Load<PackedScene>(modelPath);
				if (modelScene != null)
				{
					Node3D modelInstance = modelScene.Instantiate<Node3D>();
					targetNode.AddChild(modelInstance);
				}
			}
		}
	}

	/// <summary>
	/// Called at the end of ReadDataForObject to sync node properties to other viewports.
	/// </summary>
	public void SyncNodePropertiesToViewports(Node targetNode)
	{
		if (syncNodePropertiesCallback != null)
		{
			syncNodePropertiesCallback(targetNode);
		}
	}

	public void Client_ResetNetworking(bool isOnServer)
	{
		// add a specific player for this player
		ClientPlayer = new NetworkingPlayerState();
		ClientPlayer.IsOnServer = isOnServer;

		FramesFromServerCount = 0;
		IDToNetworkIDLookup = new HashedSlotArray();
	}

	/// <summary>
	/// Sends a packet from the client to the server.
	/// If client is on the same machine as server, calls server directly.
	/// </summary>
	public void SendPacketToServer(byte[] buffer, int size, PacketDeliveryMethod deliveryMethod)
	{
		if (ClientPlayer.IsOnServer)
		{
			// Client is local to server - call server receive directly
			if (deliveryMethod == PacketDeliveryMethod.Reliable)
			{
				Globals.worldManager_server.networkManager_server.ReceiveReliablePacketFromClient(buffer, size);
			}
			else
			{
				Globals.worldManager_server.networkManager_server.ReceiveUnreliablePacketFromClient(buffer, size);
			}
		}
		else
		{
			// TODO: Implement actual network transmission to the remote server
			if (deliveryMethod == PacketDeliveryMethod.Reliable)
			{
				// TODO: Send via TCP
			}
			else
			{
				// TODO: Send via UDP
			}
		}
	}

	/// <summary>
	/// Constructs and sends the initial TCP acknowledgment to the server.
	/// Call this after receiving and processing the initial game state.
	/// </summary>
	public void SendInitiatingTcpAck()
	{
		(byte[] buffer, int size) = ClientPlayer.ConstructInitiatingTcpAckPacket();
		SendPacketToServer(buffer, size, PacketDeliveryMethod.Reliable);
	}

	/// <summary>
	/// Constructs and sends a player input packet to the server.
	/// Call this at regular intervals (e.g., 20hz) to send player state.
	/// </summary>
	public void SendPlayerInputToServer()
	{
		(byte[] buffer, int size) = ClientPlayer.ConstructPlayerInputPacket();
		SendPacketToServer(buffer, size, PacketDeliveryMethod.Unreliable);
	}

	/// <summary>
	/// Reads a list of null-terminated strings from a buffer.
	/// Format: [count: short][string1\0][string2\0]...
	/// Returns the new offset after reading.
	/// </summary>
	protected unsafe int ReadStringListFromBuffer(byte* bufferPtr, int offset, int bufferSize, List<string> stringList)
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
	protected unsafe int ReadSharedPropertiesFromBuffer_Client(byte* bufferPtr, int startOffset, int bufferSize, short objectCount)
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
					targetNode = new NetworkedNode2D();
				}
				else
				{
					targetNode = new NetworkedNode3D();
				}
				// Add to client scene tree via callback (for multi-pass rendering support)
				if (addNodeToClientSceneCallback != null)
				{
					addNodeToClientSceneCallback(targetNode);
				}
				else
				{
					_rootSceneNode.AddChild(targetNode);
				}
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
	protected unsafe int ProcessDeletedNodesFromBuffer_Client(byte* bufferPtr, int startOffset, int bufferSize)
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
				Node nodeToDelete = GodotObject.InstanceFromId(instanceId) as Node;
				if (nodeToDelete != null)
				{
					if (deleteNodeFromClientSceneCallback != null)
					{
						deleteNodeFromClientSceneCallback(nodeToDelete);
					}
					else
					{
						nodeToDelete.QueueFree();
					}
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
	public void NewGamePacketReceived_Client(byte[] incomingBuffer, int bufferSize)
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
		ClientPlayer.ReadyForGame = true;

		// Send acknowledgment to server that we received the initial game state
		SendInitiatingTcpAck();
	}

	/// <summary>
	/// Processes a per-frame UDP packet from the server.
	/// Reads frame index, SharedProperties updates, and deleted node IDs.
	/// </summary>
	public void FramePacketReceived_Client(byte[] incomingBuffer, int bufferSize)
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
