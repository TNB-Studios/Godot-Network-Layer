using Godot;
using System;

public unsafe class SharedProperties
{
	public enum SetCompressionOnVectors
	{
		kFull,
		kHalf,
		KCompressed
	}
	
	// these settings determine how the data for specific vectors is stored in the buffer, so it can be modified as needed.
	static readonly SetCompressionOnVectors PositionCompression = SetCompressionOnVectors.kHalf;
	static readonly SetCompressionOnVectors OrientationCompression = SetCompressionOnVectors.kHalf;
	static readonly SetCompressionOnVectors ScaleCompression = SetCompressionOnVectors.kHalf;
	static readonly SetCompressionOnVectors VelocityCompression = SetCompressionOnVectors.kHalf;

	public float ViewRadius { get; set;} = 0; // only used on transmission side.
	public Vector3 Position { get; set; } = Vector3.Zero;
	public Vector3 Orientation { get; set; } = Vector3.Zero;
	public Vector3 Velocity { get; set; } = Vector3.Zero;
	public Vector3 Scale { get; set; } = Vector3.One;

	public short ObjectIndex {get; set; } = 0;

	public short CurrentModel { get; set;} = -1;
	public short CurrentAnimation { get; set; } = -1;

	public short PlayingSound {get; set; } = -1;
	public float SoundRadius {get; set;} = 10; // used server side

	public short ParticleEffect {get; set; } = -1;
	public short AttachedToObjectLookupIndex { get; set; } = -1;

	public byte[] NetworkedBlob { get; set; } = null;

	private int currentBufferOffset = 0;
	private byte* currentBuffer = null;

	public enum SharedObjectValueSetMask
	{
		kPosition = 0x01,
		kOrientation = 0x02,
		kVelocity = 0x04,
		kScale = 0x08,
		kSound = 0x10,
		kModel = 0x20,
		kAnimation = 0x40,
		kParticleEffect = 0x80,
		kIs2D = 0x100,          // Stored in top 4 bits of ObjectIndex
		kIs2DInMask = (kIs2D << 4),
		kCompressedOrientationAndVelocity = 0x200,   // Stored in top 4 bits of ObjectIndex - forces compressed format for Orientation and Velocity
		kCompressedOrientationAndVelocityInMask = (kCompressedOrientationAndVelocity << 4),
		kIsAttachedTo = 0x400,          // Stored in top 4 bits of ObjectIndex
		kIsAttachedToInMask = (kIsAttachedTo << 4),
		kUsesBlob = 0x800,          // Stored in top 4 bits of ObjectIndex
		kUsesBlobInMask = (kUsesBlob << 4),
	}

	// Constants for ObjectIndex bit manipulation
	// Top 4 bits of ObjectIndex are used for flags, leaving 12 bits for the actual index
	private const short ObjectIndexMask = 0x0FFF;        // 12 bits for actual index (max 4096 objects)

	public SharedProperties()
	{
		Position = Vector3.Zero;
		Orientation = Vector3.Zero;
		Velocity = Vector3.Zero;
		Scale = Vector3.One;
		CurrentAnimation = -1;
		ObjectIndex = 0;
		CurrentModel = -1;
		PlayingSound = -1;
	}

	void SetAddedObjectToBuffer(SharedObjectValueSetMask newPropertyToAdd)
	{
		if (currentBufferOffset == 0)
		{
			// ObjectIndex already has the kIs2DInMask and kCompressedOrientationAndVelocityInMask flags set in its top bits
			// Just write it directly to the buffer
			*(short*)(currentBuffer) = ObjectIndex;
			currentBufferOffset += 3;
			currentBuffer[2] = 0;   // this is where the mask to show what values are set is.
		}
		currentBuffer[2] |= (byte)newPropertyToAdd;
	}

	unsafe void CopyVectorToBuffer(Vector3 vector, bool is2D = false)
	{
		if (is2D)
		{
			*(Vector2*)(currentBuffer + currentBufferOffset) = new Vector2(vector.X, vector.Y);
			currentBufferOffset += sizeof(float) * 2;
		}
		else
		{
			*(Vector3*)(currentBuffer + currentBufferOffset) = vector;
			currentBufferOffset += sizeof(float) * 3;
		}
	}

	unsafe void CopyFloatToBuffer(float value)
	{
		*(float*)(currentBuffer + currentBufferOffset) = value;
		currentBufferOffset += sizeof(float);
	}

	unsafe void CopyHalfToBuffer(float value)
	{
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)value;
		currentBufferOffset += sizeof(Half);
	}

	unsafe void CopyVectorToBufferAsHalf(Vector3 vector, bool is2D = false)
	{
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)vector.X;
		*(Half*)(currentBuffer + currentBufferOffset + 2) = (Half)vector.Y;
		if (is2D)
		{
			currentBufferOffset += sizeof(Half) * 2;
		}
		else
		{
			*(Half*)(currentBuffer + currentBufferOffset + 4) = (Half)vector.Z;
			currentBufferOffset += sizeof(Half) * 3;
		}
	}

	unsafe void CopyVectorToBufferCompressed(Vector3 vector, bool is2D = false)
	{
		float length = vector.Length();
		*(Half*)(currentBuffer + currentBufferOffset) = (Half)length;
		currentBufferOffset += sizeof(Half);

		if (!is2D)
		{
			Vector3 normalized = (length > 0) ? vector / length : Vector3.Zero;
			currentBuffer[currentBufferOffset] = DirToByte(normalized);
			currentBufferOffset++;
		}
		// For 2D, we only need length since direction is implied by X,Y components
	}

	// Static read functions for use by ReadDataForObject
	static unsafe Vector3 ReadVectorFromBufferStatic(byte* buffer, ref int offset, bool is2D)
	{
		Vector3 result;
		if (is2D)
		{
			result = new Vector3(
				*(float*)(buffer + offset),
				*(float*)(buffer + offset + 4),
				0
			);
			offset += sizeof(float) * 2;
		}
		else
		{
			result = *(Vector3*)(buffer + offset);
			offset += sizeof(float) * 3;
		}
		return result;
	}

	static unsafe Vector3 ReadVectorFromBufferAsHalfStatic(byte* buffer, ref int offset, bool is2D)
	{
		float x = (float)*(Half*)(buffer + offset);
		float y = (float)*(Half*)(buffer + offset + 2);
		float z = 0;
		if (is2D)
		{
			offset += sizeof(Half) * 2;
		}
		else
		{
			z = (float)*(Half*)(buffer + offset + 4);
			offset += sizeof(Half) * 3;
		}
		return new Vector3(x, y, z);
	}

	static unsafe Vector3 ReadVectorFromBufferCompressedStatic(byte* buffer, ref int offset, bool is2D)
	{
		float length = (float)*(Half*)(buffer + offset);
		offset += sizeof(Half);

		if (is2D)
		{
			// For 2D compressed, we don't have a direction byte
			// This mode isn't as useful for 2D, return just the length as X
			return new Vector3(length, 0, 0);
		}
		else
		{
			byte dirByte = buffer[offset];
			offset++;
			Vector3 normalized = ByteToDir(dirByte);
			return normalized * length;
		}
	}

	/// <summary>
	/// Reads object data from buffer and applies to target node.
	/// The mask parameter should include the extended flags (kIs2D, kCompressedOrientationAndVelocity) extracted from ObjectIndex.
	/// </summary>
	public static unsafe int ReadDataForObject(byte* buffer, Node targetNode, short mask)
	{
		int offset = 3; // Skip ObjectIndex (2 bytes) + Mask (1 byte)
		bool is2D = (mask & (short)SharedObjectValueSetMask.kIs2D) != 0;
		bool compressedOrientationAndVelocity = (mask & (short)SharedObjectValueSetMask.kCompressedOrientationAndVelocity) != 0;

		// Get the 3D node for setting properties (or null if 2D)
		Node3D node3D = targetNode as Node3D;
		Node2D node2D = targetNode as Node2D;

		// If attached to another object, read the 2-byte lookup index and skip transform data
		if ((mask & (short)SharedObjectValueSetMask.kIsAttachedTo) != 0)
		{
			short attachedIndex = *(short*)(buffer + offset);
			offset += sizeof(short);
			Globals.worldManager_client.networkManager_client.ApplyAttachToNode(targetNode, attachedIndex);
			// Skip to sound/model/animation/particle — no position/orientation/velocity/scale
			goto AfterTransformData;
		}

        // Velocity - use compressed format if flag is set AND not 2D (compressed uses 3D direction table)
        // Receiving velocity means the object is not attached (attached objects skip transform data),
        // so detach in case the object was previously attached.
        if ((mask & (short)SharedObjectValueSetMask.kVelocity) != 0)
        {
            SetCompressionOnVectors velocityMode = (compressedOrientationAndVelocity && !is2D)
                ? SetCompressionOnVectors.KCompressed
                : VelocityCompression;
            Vector3 velocity = ReadVectorByCompression(buffer, ref offset, velocityMode, is2D);
            if (targetNode is NetworkedNode3D networkedNode3D)
            {
                networkedNode3D.Velocity = velocity;
            }
            else if (targetNode is NetworkedNode2D networkedNode2D)
            {
                networkedNode2D.Velocity = new Vector2(velocity.X, velocity.Y);
            }

            // If this node was attached, detach it (route through callback/default reparent)
            if (targetNode is INetworkedNode nn && nn.attachedToObjectLookupIndex != -1)
            {
                Globals.worldManager_client.networkManager_client.ApplyAttachToNode(targetNode, -1);
            }
        }

        // Position (never uses compressed format, only Full or Half)
        if ((mask & (short)SharedObjectValueSetMask.kPosition) != 0)
		{
			Vector3 position = ReadVectorByCompression(buffer, ref offset, PositionCompression, is2D);
			if (targetNode is INetworkedNode nnPos)
			{
				NetworkedNodeHelper.ApplyNetworkPosition(targetNode, nnPos, position);
			}
			else if (node3D != null)
				node3D.GlobalPosition = position;
			else if (node2D != null)
				node2D.GlobalPosition = new Vector2(position.X, position.Y);
		}

		// Orientation - use compressed format if flag is set AND not 2D (compressed uses 3D direction table)
		if ((mask & (short)SharedObjectValueSetMask.kOrientation) != 0)
		{
			SetCompressionOnVectors orientationMode = (compressedOrientationAndVelocity && !is2D)
				? SetCompressionOnVectors.KCompressed
				: OrientationCompression;
			Vector3 orientation = ReadVectorByCompression(buffer, ref offset, orientationMode, is2D);
			if (targetNode is INetworkedNode nnOri)
			{
				NetworkedNodeHelper.ApplyNetworkOrientation(targetNode, nnOri, orientation);
			}
			else if (node3D != null)
				node3D.Rotation = orientation;
			else if (node2D != null)
				node2D.Rotation = orientation.Y;
		}

		// Scale
		if ((mask & (short)SharedObjectValueSetMask.kScale) != 0)
		{
			Vector3 scale = ReadVectorByCompression(buffer, ref offset, ScaleCompression, is2D);
			if (targetNode is INetworkedNode nnScale)
			{
				NetworkedNodeHelper.ApplyNetworkScale(targetNode, nnScale, scale);
			}
			else if (node3D != null)
				node3D.Scale = scale;
			else if (node2D != null)
				node2D.Scale = new Vector2(scale.X, scale.Y);
		}

		AfterTransformData:

		// Sound
		if ((mask & (short)SharedObjectValueSetMask.kSound) != 0)
		{
			short playingSound;
			if (Globals.worldManager_client.networkManager_client.SoundNames.Count > 255)
			{
				playingSound = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				playingSound = (sbyte)buffer[offset];
				offset++;
			}
	
            // Determine sound parameters and route through callback
            float soundRadius = 0;
            bool soundIs2D = false;
            short actualSoundIndex = playingSound;

            if (playingSound < -1)
            {
                // 2D sound - decode the index
                soundIs2D = true;
                actualSoundIndex = (short)((-playingSound) - 2);
            }
            else if (playingSound > -1)
            {
                // 3D sound - read radius
                soundRadius = (float)buffer[offset];
                offset++;
            }

            Globals.worldManager_client.networkManager_client.ApplySoundToNode(targetNode, actualSoundIndex, soundRadius, soundIs2D);
		}

		// Model
		if ((mask & (short)SharedObjectValueSetMask.kModel) != 0)
		{
			short currentModel;
			if (Globals.worldManager_client.networkManager_client.ModelNames.Count > 255)
			{
				currentModel = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				currentModel = buffer[offset];
				offset++;
			}

			// Store the model index on the NetworkedNode
			if (targetNode is INetworkedNode networkedNodeModel)
			{
				networkedNodeModel.currentModelIndex = currentModel;
			}

			// Apply model via callback (handles multi-pass rendering)
			Globals.worldManager_client.networkManager_client.ApplyModelToNode(targetNode, currentModel);
		}

		// Animation
		if ((mask & (short)SharedObjectValueSetMask.kAnimation) != 0)
		{
			short currentAnimation;
			if (Globals.worldManager_client.networkManager_client.AnimationNames.Count > 255)
			{
				currentAnimation = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				currentAnimation = buffer[offset];
				offset++;
			}

			// Store the animation index on the NetworkedNode
			if (targetNode is INetworkedNode networkedNodeAnim)
			{
				networkedNodeAnim.currentAnimationIndex = currentAnimation;
			}

			Globals.worldManager_client.networkManager_client.ApplyAnimationToNode(targetNode, currentAnimation);
		}

		// Particle Effect
		if ((mask & (short)SharedObjectValueSetMask.kParticleEffect) != 0)
		{
			short particleEffect;
			if (Globals.worldManager_client.networkManager_client.ParticleEffectNames.Count > 255)
			{
				particleEffect = *(short*)(buffer + offset);
				offset += 2;
			}
			else
			{
				particleEffect = buffer[offset];
				offset++;
			}

			// Store the particle effect index on the NetworkedNode
			if (targetNode is INetworkedNode networkedNodeParticle)
			{
				networkedNodeParticle.currentParticleEffectIndex = particleEffect;
			}

			Globals.worldManager_client.networkManager_client.ApplyParticleEffectToNode(targetNode, particleEffect);
		}

		// Blob data
		if ((mask & (short)SharedObjectValueSetMask.kUsesBlob) != 0)
		{
			byte blobLength = buffer[offset];
			offset++;
			byte[] blob = new byte[blobLength];
			for (int i = 0; i < blobLength; i++)
			{
				blob[i] = buffer[offset + i];
			}
			offset += blobLength;

			if (targetNode is INetworkedNode networkedNodeBlob)
			{
				networkedNodeBlob.SetNetworkedBlob(blob);
			}
		}

		// Sync all node properties (position, rotation, scale) to other viewports
		Globals.worldManager_client.networkManager_client.SyncNodePropertiesToViewports(targetNode);

		return offset;
	}

	/// <summary>
	/// Helper to read a vector using the specified compression mode.
	/// </summary>
	private static unsafe Vector3 ReadVectorByCompression(byte* buffer, ref int offset, SetCompressionOnVectors compression, bool is2D)
	{
		return compression switch
		{
			SetCompressionOnVectors.kFull => ReadVectorFromBufferStatic(buffer, ref offset, is2D),
			SetCompressionOnVectors.kHalf => ReadVectorFromBufferAsHalfStatic(buffer, ref offset, is2D),
			SetCompressionOnVectors.KCompressed => ReadVectorFromBufferCompressedStatic(buffer, ref offset, is2D),
			_ => ReadVectorFromBufferStatic(buffer, ref offset, is2D)
		};
	}

	/// <summary>
	/// Extracts the full mask (including extended flags) from an encoded object index.
	/// Also returns the actual object index with flags masked off.
	/// </summary>
	public static void DecodeObjectIndexAndMask(short encodedIndex, byte baseMask, out short objectIndex, out short fullMask)
	{
		// Extract the actual object index (lower 12 bits)
		objectIndex = (short)(encodedIndex & ObjectIndexMask);

		// Extract extended flags from top 4 bits (12-15) and shift to mask positions (8-11)
		// kIs2DInMask (bit 12) -> kIs2D (0x100), kCompressedInMask (bit 13) -> 0x200,
		// kIsAttachedToInMask (bit 14) -> 0x400, kUsesBlobInMask (bit 15) -> 0x800
		short extendedFlags = (short)((encodedIndex >> 4) & 0xF00);

		// Combine base mask with extended flags
		fullMask = (short)((ushort)baseMask | (ushort)extendedFlags);
	}

	public unsafe int ConstructDataForObject(byte* buffer, int spaceLeftInBytes, SharedProperties oldSharedProperties)
	{
		currentBuffer = buffer;
		currentBufferOffset = 0;

		// Check if blob should be sent (must set flag on ObjectIndex BEFORE it gets written to buffer)
		bool sendBlob = false;
		if (NetworkedBlob != null && NetworkedBlob.Length > 0)
		{
			if (oldSharedProperties == null || !BlobEquals(NetworkedBlob, oldSharedProperties.NetworkedBlob))
			{
				sendBlob = true;
				ObjectIndex |= unchecked((short)SharedObjectValueSetMask.kUsesBlobInMask);
			}
		}

		// Extract flags from ObjectIndex (stored in top bits)
		bool is2D = (ObjectIndex & (short)SharedObjectValueSetMask.kIs2DInMask) != 0;
		bool compressedOrientationAndVelocity = (ObjectIndex & unchecked((short)SharedObjectValueSetMask.kCompressedOrientationAndVelocityInMask)) != 0;
		bool isAttachedTo = (ObjectIndex & unchecked((short)SharedObjectValueSetMask.kIsAttachedToInMask)) != 0;

		// If attached to another object, write the 2-byte lookup index only if changed, and skip all transform data
		if (isAttachedTo)
		{
			if (oldSharedProperties == null || AttachedToObjectLookupIndex != oldSharedProperties.AttachedToObjectLookupIndex)
			{
				SetAddedObjectToBuffer(0); // ensure ObjectIndex and mask header are written
				*(short*)(currentBuffer + currentBufferOffset) = AttachedToObjectLookupIndex;
				currentBufferOffset += sizeof(short);
			}
			else
			{
				// Attached index unchanged - clear the flag so client doesn't try to read it
				ObjectIndex &= unchecked((short)~SharedObjectValueSetMask.kIsAttachedToInMask);
			}
			// Skip transform data regardless — position comes from parent
			goto AfterTransformData;
		}

		// start looking at each value to see what we might put in the buffer for this object.
		// Note: Position and Scale never use compressed format, only Full or Half
		bool sendVelocity = false;
		if (oldSharedProperties == null || CompareVector(Velocity, oldSharedProperties.Velocity))
		{	
			sendVelocity = true;	
			if (oldSharedProperties == null)
			{
				if (Velocity == Vector3.Zero)
				{
					sendVelocity = false;
				}
			}
			if (sendVelocity)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kVelocity);
				// Use compressed format if flag is set AND not 2D (compressed uses 3D direction table)
				if (compressedOrientationAndVelocity && !is2D)
				{
					CopyVectorToBufferCompressed(Velocity, is2D);
				}
				else if (VelocityCompression == SetCompressionOnVectors.kFull)
				{
					CopyVectorToBuffer(Velocity, is2D);
				}
				else if (VelocityCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Velocity, is2D);
				}
				else
				{
					CopyVectorToBufferCompressed(Velocity, is2D);
				}
			}
		}
		if (oldSharedProperties == null || CompareVector(Position, oldSharedProperties.Position))
		{
			bool sendPosition = true;
			if (oldSharedProperties == null)
			{
				if (Position == Vector3.Zero)
				{
					sendPosition = false;
				}
			}

			if (sendPosition)
			{
				// so, if we didn't send a velocity, but there IS a velocity, and it's not the initial set up of the object, don't bother sending a positional update
				// there's no need, because the object is moving anyway. We DO want to send a new position if the velocity has changed, just to be sure the object is in the right place
				if (!sendVelocity && Velocity != Vector3.Zero && oldSharedProperties != null)
				{
					sendPosition = false;
				}
			}
			if (sendPosition)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kPosition);
				if (PositionCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Position, is2D);
				}
				else  // kFull (default for position)
				{
					CopyVectorToBuffer(Position, is2D);
				}
			}
		}
		if (oldSharedProperties == null || CompareVector(Orientation, oldSharedProperties.Orientation))
		{
			bool sendOrientation = true;
			if (oldSharedProperties == null)
			{
				if (Orientation == Vector3.Zero)
				{
					sendOrientation = false;
				}
			}
			if (sendOrientation)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kOrientation);
				// Use compressed format if flag is set AND not 2D (compressed uses 3D direction table)
				if (compressedOrientationAndVelocity && !is2D)
				{
					CopyVectorToBufferCompressed(Orientation, is2D);
				}
				else if (OrientationCompression == SetCompressionOnVectors.kFull)
				{
					CopyVectorToBuffer(Orientation, is2D);
				}
				else if (OrientationCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Orientation, is2D);
				}
				else
				{
					CopyVectorToBufferCompressed(Orientation, is2D);
				}
			}
		}
		// Note: Scale never uses compressed format, only Full or Half
		if (oldSharedProperties == null || CompareVector(Scale, oldSharedProperties.Scale))
		{
			bool sendScale = true;
			if (oldSharedProperties == null)
			{
				if (Scale == Vector3.One)
				{
					sendScale = false;
				}
			}
			if (sendScale)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kScale);
				if (ScaleCompression == SetCompressionOnVectors.kHalf)
				{
					CopyVectorToBufferAsHalf(Scale, is2D);
				}
				else  // kFull (default for scale)
				{
					CopyVectorToBuffer(Scale, is2D);
				}
			}
		}

		AfterTransformData:

		if (oldSharedProperties == null || PlayingSound != oldSharedProperties.PlayingSound)
		{
			bool sendSound = true;
			if (oldSharedProperties == null)
			{
				if (PlayingSound == -1)
				{
					sendSound = false;
				}
			}
			if (sendSound)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kSound);
				if (Globals.worldManager_server.networkManager_server.SoundNames.Count > 255)
				{
					*(short*)(currentBuffer + currentBufferOffset) = PlayingSound;
					currentBufferOffset += 2;
				}
				else
				{
					currentBuffer[currentBufferOffset] = (byte)PlayingSound;
					currentBufferOffset++;
				}
				// only send radius if we are playing a 3D sound, ie the PlayingSound value is not > 0
				if (PlayingSound > -1)
				{
					currentBuffer[currentBufferOffset] = (byte)Mathf.Clamp(SoundRadius, 0, 255);
					currentBufferOffset++;
				}
			}
		}
		if (oldSharedProperties == null || CurrentModel != oldSharedProperties.CurrentModel)
		{
			bool sendModel = true;
			if (oldSharedProperties == null)
			{
				if (CurrentModel == -1)
				{
					sendModel = false;
				}
			}
			if (sendModel)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kModel);
				if (Globals.worldManager_server.networkManager_server.ModelNames.Count > 255)
				{
					*(short*)(currentBuffer + currentBufferOffset) = CurrentModel;
					currentBufferOffset += 2;
				}
				else
				{
					currentBuffer[currentBufferOffset] = (byte)CurrentModel;
					currentBufferOffset++;
				}
			}
		}
		if (oldSharedProperties == null || CurrentAnimation != oldSharedProperties.CurrentAnimation)
		{
			bool sendAnimation = true;
			if (oldSharedProperties == null)
			{
				if (CurrentAnimation == -1)
				{
					sendAnimation = false;
				}
			}
			if (sendAnimation)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kAnimation);
				if (Globals.worldManager_server.networkManager_server.AnimationNames.Count > 255)
				{
					*(short*)(currentBuffer + currentBufferOffset) = CurrentAnimation;
					currentBufferOffset += 2;
				}
				else
				{
					currentBuffer[currentBufferOffset] = (byte)CurrentAnimation;
					currentBufferOffset++;
				}
			}
		}
		if (oldSharedProperties == null || ParticleEffect != oldSharedProperties.ParticleEffect)
		{
			bool sendParticleEffect = true;
			if (oldSharedProperties == null)
			{
				if (ParticleEffect == -1)
				{
					sendParticleEffect = false;
				}
			}
			if (sendParticleEffect)
			{
				SetAddedObjectToBuffer(SharedObjectValueSetMask.kParticleEffect);
				if (Globals.worldManager_server.networkManager_server.ParticleEffectNames.Count > 255)
				{
					*(short*)(currentBuffer + currentBufferOffset) = ParticleEffect;
					currentBufferOffset += 2;
				}
				else
				{
					currentBuffer[currentBufferOffset] = (byte)ParticleEffect;
					currentBufferOffset++;
				}
			}
		}
		// Blob data (signaled via kUsesBlob flag in ObjectIndex top bits)
		if (sendBlob)
		{
			SetAddedObjectToBuffer(0); // ensure header is written
			currentBuffer[currentBufferOffset] = (byte)NetworkedBlob.Length;
			currentBufferOffset++;
			for (int i = 0; i < NetworkedBlob.Length; i++)
			{
				currentBuffer[currentBufferOffset + i] = NetworkedBlob[i];
			}
			currentBufferOffset += NetworkedBlob.Length;
		}
		return currentBufferOffset;
	}

	public static bool CompareVector(Vector3 vector1, Vector3 vector2)
	{
		return vector1.X != vector2.X || vector1.Y != vector2.Y || vector1.Z != vector2.Z;
	}

	private static bool BlobEquals(byte[] a, byte[] b)
	{
		if (a == null && b == null) return true;
		if (a == null || b == null) return false;
		if (a.Length != b.Length) return false;
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i]) return false;
		}
		return true;
	}

	// Quake III normal vector compression lookup table (162 pre-computed normalized vectors)
	// Source: id-Software/Quake-III-Arena (GPL)
	private static readonly Vector3[] ByteDirs = new Vector3[]
	{
		new Vector3(-0.525731f, 0.000000f, 0.850651f), new Vector3(-0.442863f, 0.238856f, 0.864188f),
		new Vector3(-0.295242f, 0.000000f, 0.955423f), new Vector3(-0.309017f, 0.500000f, 0.809017f),
		new Vector3(-0.162460f, 0.262866f, 0.951056f), new Vector3(0.000000f, 0.000000f, 1.000000f),
		new Vector3(0.000000f, 0.850651f, 0.525731f), new Vector3(-0.147621f, 0.716567f, 0.681718f),
		new Vector3(0.147621f, 0.716567f, 0.681718f), new Vector3(0.000000f, 0.525731f, 0.850651f),
		new Vector3(0.309017f, 0.500000f, 0.809017f), new Vector3(0.525731f, 0.000000f, 0.850651f),
		new Vector3(0.295242f, 0.000000f, 0.955423f), new Vector3(0.442863f, 0.238856f, 0.864188f),
		new Vector3(0.162460f, 0.262866f, 0.951056f), new Vector3(-0.681718f, 0.147621f, 0.716567f),
		new Vector3(-0.809017f, 0.309017f, 0.500000f), new Vector3(-0.587785f, 0.425325f, 0.688191f),
		new Vector3(-0.850651f, 0.525731f, 0.000000f), new Vector3(-0.864188f, 0.442863f, 0.238856f),
		new Vector3(-0.716567f, 0.681718f, 0.147621f), new Vector3(-0.688191f, 0.587785f, 0.425325f),
		new Vector3(-0.500000f, 0.809017f, 0.309017f), new Vector3(-0.238856f, 0.864188f, 0.442863f),
		new Vector3(-0.425325f, 0.688191f, 0.587785f), new Vector3(-0.716567f, 0.681718f, -0.147621f),
		new Vector3(-0.500000f, 0.809017f, -0.309017f), new Vector3(-0.525731f, 0.850651f, 0.000000f),
		new Vector3(0.000000f, 0.850651f, -0.525731f), new Vector3(-0.238856f, 0.864188f, -0.442863f),
		new Vector3(0.000000f, 0.955423f, -0.295242f), new Vector3(-0.262866f, 0.951056f, -0.162460f),
		new Vector3(0.000000f, 1.000000f, 0.000000f), new Vector3(0.000000f, 0.955423f, 0.295242f),
		new Vector3(-0.262866f, 0.951056f, 0.162460f), new Vector3(0.238856f, 0.864188f, 0.442863f),
		new Vector3(0.262866f, 0.951056f, 0.162460f), new Vector3(0.500000f, 0.809017f, 0.309017f),
		new Vector3(0.238856f, 0.864188f, -0.442863f), new Vector3(0.262866f, 0.951056f, -0.162460f),
		new Vector3(0.500000f, 0.809017f, -0.309017f), new Vector3(0.850651f, 0.525731f, 0.000000f),
		new Vector3(0.716567f, 0.681718f, 0.147621f), new Vector3(0.716567f, 0.681718f, -0.147621f),
		new Vector3(0.525731f, 0.850651f, 0.000000f), new Vector3(0.425325f, 0.688191f, 0.587785f),
		new Vector3(0.864188f, 0.442863f, 0.238856f), new Vector3(0.688191f, 0.587785f, 0.425325f),
		new Vector3(0.809017f, 0.309017f, 0.500000f), new Vector3(0.681718f, 0.147621f, 0.716567f),
		new Vector3(0.587785f, 0.425325f, 0.688191f), new Vector3(0.955423f, 0.295242f, 0.000000f),
		new Vector3(1.000000f, 0.000000f, 0.000000f), new Vector3(0.951056f, 0.162460f, 0.262866f),
		new Vector3(0.850651f, -0.525731f, 0.000000f), new Vector3(0.955423f, -0.295242f, 0.000000f),
		new Vector3(0.864188f, -0.442863f, 0.238856f), new Vector3(0.951056f, -0.162460f, 0.262866f),
		new Vector3(0.809017f, -0.309017f, 0.500000f), new Vector3(0.681718f, -0.147621f, 0.716567f),
		new Vector3(0.850651f, 0.000000f, 0.525731f), new Vector3(0.864188f, 0.442863f, -0.238856f),
		new Vector3(0.809017f, 0.309017f, -0.500000f), new Vector3(0.951056f, 0.162460f, -0.262866f),
		new Vector3(0.525731f, 0.000000f, -0.850651f), new Vector3(0.681718f, 0.147621f, -0.716567f),
		new Vector3(0.681718f, -0.147621f, -0.716567f), new Vector3(0.850651f, 0.000000f, -0.525731f),
		new Vector3(0.809017f, -0.309017f, -0.500000f), new Vector3(0.864188f, -0.442863f, -0.238856f),
		new Vector3(0.951056f, -0.162460f, -0.262866f), new Vector3(0.147621f, 0.716567f, -0.681718f),
		new Vector3(0.309017f, 0.500000f, -0.809017f), new Vector3(0.425325f, 0.688191f, -0.587785f),
		new Vector3(0.442863f, 0.238856f, -0.864188f), new Vector3(0.587785f, 0.425325f, -0.688191f),
		new Vector3(0.688191f, 0.587785f, -0.425325f), new Vector3(-0.147621f, 0.716567f, -0.681718f),
		new Vector3(-0.309017f, 0.500000f, -0.809017f), new Vector3(0.000000f, 0.525731f, -0.850651f),
		new Vector3(-0.525731f, 0.000000f, -0.850651f), new Vector3(-0.442863f, 0.238856f, -0.864188f),
		new Vector3(-0.295242f, 0.000000f, -0.955423f), new Vector3(-0.162460f, 0.262866f, -0.951056f),
		new Vector3(0.000000f, 0.000000f, -1.000000f), new Vector3(0.295242f, 0.000000f, -0.955423f),
		new Vector3(0.162460f, 0.262866f, -0.951056f), new Vector3(-0.442863f, -0.238856f, -0.864188f),
		new Vector3(-0.309017f, -0.500000f, -0.809017f), new Vector3(-0.162460f, -0.262866f, -0.951056f),
		new Vector3(0.000000f, -0.850651f, -0.525731f), new Vector3(-0.147621f, -0.716567f, -0.681718f),
		new Vector3(0.147621f, -0.716567f, -0.681718f), new Vector3(0.000000f, -0.525731f, -0.850651f),
		new Vector3(0.309017f, -0.500000f, -0.809017f), new Vector3(0.442863f, -0.238856f, -0.864188f),
		new Vector3(0.162460f, -0.262866f, -0.951056f), new Vector3(0.238856f, -0.864188f, -0.442863f),
		new Vector3(0.500000f, -0.809017f, -0.309017f), new Vector3(0.425325f, -0.688191f, -0.587785f),
		new Vector3(0.716567f, -0.681718f, -0.147621f), new Vector3(0.688191f, -0.587785f, -0.425325f),
		new Vector3(0.587785f, -0.425325f, -0.688191f), new Vector3(0.000000f, -0.955423f, -0.295242f),
		new Vector3(0.000000f, -1.000000f, 0.000000f), new Vector3(0.262866f, -0.951056f, -0.162460f),
		new Vector3(0.000000f, -0.850651f, 0.525731f), new Vector3(0.000000f, -0.955423f, 0.295242f),
		new Vector3(0.238856f, -0.864188f, 0.442863f), new Vector3(0.262866f, -0.951056f, 0.162460f),
		new Vector3(0.500000f, -0.809017f, 0.309017f), new Vector3(0.716567f, -0.681718f, 0.147621f),
		new Vector3(0.525731f, -0.850651f, 0.000000f), new Vector3(-0.238856f, -0.864188f, -0.442863f),
		new Vector3(-0.500000f, -0.809017f, -0.309017f), new Vector3(-0.262866f, -0.951056f, -0.162460f),
		new Vector3(-0.850651f, -0.525731f, 0.000000f), new Vector3(-0.716567f, -0.681718f, -0.147621f),
		new Vector3(-0.716567f, -0.681718f, 0.147621f), new Vector3(-0.525731f, -0.850651f, 0.000000f),
		new Vector3(-0.500000f, -0.809017f, 0.309017f), new Vector3(-0.238856f, -0.864188f, 0.442863f),
		new Vector3(-0.262866f, -0.951056f, 0.162460f), new Vector3(-0.864188f, -0.442863f, 0.238856f),
		new Vector3(-0.809017f, -0.309017f, 0.500000f), new Vector3(-0.688191f, -0.587785f, 0.425325f),
		new Vector3(-0.681718f, -0.147621f, 0.716567f), new Vector3(-0.442863f, -0.238856f, 0.864188f),
		new Vector3(-0.587785f, -0.425325f, 0.688191f), new Vector3(-0.309017f, -0.500000f, 0.809017f),
		new Vector3(-0.147621f, -0.716567f, 0.681718f), new Vector3(-0.425325f, -0.688191f, 0.587785f),
		new Vector3(-0.162460f, -0.262866f, 0.951056f), new Vector3(0.442863f, -0.238856f, 0.864188f),
		new Vector3(0.162460f, -0.262866f, 0.951056f), new Vector3(0.309017f, -0.500000f, 0.809017f),
		new Vector3(0.147621f, -0.716567f, 0.681718f), new Vector3(0.000000f, -0.525731f, 0.850651f),
		new Vector3(0.425325f, -0.688191f, 0.587785f), new Vector3(0.587785f, -0.425325f, 0.688191f),
		new Vector3(0.688191f, -0.587785f, 0.425325f), new Vector3(-0.955423f, 0.295242f, 0.000000f),
		new Vector3(-0.951056f, 0.162460f, 0.262866f), new Vector3(-1.000000f, 0.000000f, 0.000000f),
		new Vector3(-0.850651f, 0.000000f, 0.525731f), new Vector3(-0.955423f, -0.295242f, 0.000000f),
		new Vector3(-0.951056f, -0.162460f, 0.262866f), new Vector3(-0.864188f, 0.442863f, -0.238856f),
		new Vector3(-0.951056f, 0.162460f, -0.262866f), new Vector3(-0.809017f, 0.309017f, -0.500000f),
		new Vector3(-0.864188f, -0.442863f, -0.238856f), new Vector3(-0.951056f, -0.162460f, -0.262866f),
		new Vector3(-0.809017f, -0.309017f, -0.500000f), new Vector3(-0.681718f, 0.147621f, -0.716567f),
		new Vector3(-0.681718f, -0.147621f, -0.716567f), new Vector3(-0.850651f, 0.000000f, -0.525731f),
		new Vector3(-0.688191f, 0.587785f, -0.425325f), new Vector3(-0.587785f, 0.425325f, -0.688191f),
		new Vector3(-0.425325f, 0.688191f, -0.587785f), new Vector3(-0.425325f, -0.688191f, -0.587785f),
		new Vector3(-0.587785f, -0.425325f, -0.688191f), new Vector3(-0.688191f, -0.587785f, -0.425325f)
	};

	/// <summary>
	/// Compresses a normalized direction vector to a single byte index.
	/// Finds the closest matching pre-computed normal from the lookup table.
	/// </summary>
	public static byte DirToByte(Vector3 dir)
	{
		if (dir == Vector3.Zero)
		{
			return 0;
		}

		float bestDot = 0;
		byte best = 0;
		for (int i = 0; i < ByteDirs.Length; i++)
		{
			float dot = dir.X * ByteDirs[i].X + dir.Y * ByteDirs[i].Y + dir.Z * ByteDirs[i].Z;
			if (dot > bestDot)
			{
				bestDot = dot;
				best = (byte)i;
			}
		}
		return best;
	}

	/// <summary>
	/// Decompresses a byte index back to a normalized direction vector.
	/// </summary>
	public static Vector3 ByteToDir(byte b)
	{
		if (b >= ByteDirs.Length)
		{
			return Vector3.Zero;
		}
		return ByteDirs[b];
	}

	/// <summary>
	/// Returns the byte size for a vector based on its compression setting and 2D mode.
	/// </summary>
	private static int GetVectorSizeForCompression(SetCompressionOnVectors compression, bool is2D)
	{
		if (is2D)
		{
			return compression switch
			{
				SetCompressionOnVectors.kFull => 8,        // 2 floats (4 bytes each)
				SetCompressionOnVectors.kHalf => 4,        // 2 halfs (2 bytes each)
				SetCompressionOnVectors.KCompressed => 2,  // half magnitude only (no direction for 2D)
				_ => 8
			};
		}
		else
		{
			return compression switch
			{
				SetCompressionOnVectors.kFull => 12,       // 3 floats (4 bytes each)
				SetCompressionOnVectors.kHalf => 6,        // 3 halfs (2 bytes each)
				SetCompressionOnVectors.KCompressed => 3,  // half magnitude (2) + byte direction (1)
				_ => 12
			};
		}
	}

	/*
	/// <summary>
	/// Calculates the total byte size of a SharedProperty entry given its mask.
	/// This works because compression settings are compile-time constants.
	/// The mask should be a short to include the extended flags (kIs2D, kCompressedOrientationAndVelocity).
	/// </summary>
	public static int CalculateSizeFromMask(short mask)
	{
		int size = 3; // ObjectIndex (2 bytes) + Mask (1 byte)
		bool is2D = (mask & (short)SharedObjectValueSetMask.kIs2D) != 0;
		bool compressedOrientationAndVelocity = (mask & (short)SharedObjectValueSetMask.kCompressedOrientationAndVelocity) != 0;

		if ((mask & (short)SharedObjectValueSetMask.kPosition) != 0)
		{
			size += GetVectorSizeForCompression(PositionCompression, is2D);
		}

		if ((mask & (short)SharedObjectValueSetMask.kOrientation) != 0)
		{
			var orientationMode = (compressedOrientationAndVelocity && !is2D) ? SetCompressionOnVectors.KCompressed : OrientationCompression;
			size += GetVectorSizeForCompression(orientationMode, is2D);
		}

		if ((mask & (short)SharedObjectValueSetMask.kScale) != 0)
			size += GetVectorSizeForCompression(ScaleCompression, is2D);

		if ((mask & (short)SharedObjectValueSetMask.kVelocity) != 0)
		{
			var velocityMode = (compressedOrientationAndVelocity && !is2D) ? SetCompressionOnVectors.KCompressed : VelocityCompression;
			size += GetVectorSizeForCompression(velocityMode, is2D);
		}

		if ((mask & (short)SharedObjectValueSetMask.kSound) != 0)
		{
			size += (Globals.networkManager.SoundNames.Count > 255) ? 2 : 1;  // Sound index
			size += 1;  // SoundRadius as byte
		}

		if ((mask & (short)SharedObjectValueSetMask.kModel) != 0)
			size += (Globals.networkManager.ModelNames.Count > 255) ? 2 : 1;

		if ((mask & (short)SharedObjectValueSetMask.kAnimation) != 0)
			size += (Globals.networkManager.AnimationNames.Count > 255) ? 2 : 1;

		if ((mask & (short)SharedObjectValueSetMask.kParticleEffect) != 0)
			size += (Globals.networkManager.ParticleEffectNames.Count > 255) ? 2 : 1;

		return size;
	}
	*/
}
