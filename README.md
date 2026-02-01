Godot 4.5 C# Quake 3 like client/server networking architecture.

This is designed very much like how Quake III's networking works, since Godot doesn't have anything like this built in.

The code will come down as a directory called Networking (I have it set up as a SubModule in my test harness app). You don't have to, or you can if you want. Your choice.

The tl;dr version is this.

* It's a client/server architecture, where one machine running the game is deemed the server. Other players will connect to that machine, and that's the machine that is actively playing the game.
* It can handle about 150 or so objects updating per frame, but can handle 16000 objects in scene.
* You can attach models, play animations, play sounds, play particle effects on any object added to the scene that you inform the networking system exists.
* It's designed for fast moving FPS/Space Sim etc kind of games, without a million objects. You should not be using this for RTS like games, or anything with a LOT of enemies (so no Rogue Likes!)
* It's intended for self hosting games, where the player is also the server. Not intended for backend servers, although this does have the capability to run in headless mode.
* You will need something else for account handling and lobbies - this is one thing that this code doesn't handle. It expects you to get IP addresses for all players yourself, so get used to the idea of using Braincloud or Playfab or some such for that.


So how does Quake III's networking work?

Well, firstly, you have to get the concept of Client and Server. The code is actually different based on which your game is running as (and it has no problem with one machine being both a server AND a client, and having other remote clients attach.)

You can run it in just pure server mode, or just pure client mode.

The server runs at 20hz - because running any faster floods your network with packets from the server to the client. Almost everything sent is via UDP, although there is one TCP/IP packet sent initially, with initial game state in it.

So how does Quake III's networking work? To handle fast updates (ie UDP - TCP/IP will break up large messages into UDP packets and then has a complicated "in order, required ack" system that reconstructs the whole TCP/IP packet on the other end, but it introduces a lot of latency because it requires acks back and forth for each part delivered, and there's a time out waiting for acks for parts.)
All of Quake III's packets per frame from both Server to client and client to server are UDP.

The problem is that UDP packets can often disappear or arrive out of order.

So Quake III's networking is designed to cope with that. It does it by hold an array of states on the server - all object positions, orientations, scales, velocities, what sounds they are playing, when models/animations they are playing and what particle effects they might be playing - in a big old array.
That array of states is added to every time the server sends a frame state to the client. When the client actually gets a frame state of all objects that have differing values, it updates the positions etc of all it's objects it's displaying and then sends back to the server an ack packet, that also contains all current controller input.

In this way, the server knows "Oh, client 1 got frameState 45, so I know that from now on, I will send it ONLY the things that have changed for all my objects between frameState 45 and what my current state is. And it deletes all the framestates before 45, since it doens't need them any more.
This way, even if a framestate being sent to the client gets lost, as long as a later one arrives, it's still only sending objects that have changed from the last time it knew the client got an update.

This helps keep UDP packets small (they can only be about 1400 bytes, since that's all the size that the Internet Routers will allow. Any larger and they just get dropped or fragmented), so we have to keep all these updates as small as possible.

With that in mind, we only ever send that which has changed. If position hasn't changed, we don't send it. It velocity or scale hasn't changed, we don't send it.

Now the flow of functions called works like this.

Set up NetworkManager_Server, if you are running a server.
Set up NetworkManager_Client if you are running as a client (note, as mentioned, it's fine to have both.)

it's best to think about a client as literally a dumb client. All it does is get stuff from the server that says "This object is here, and uses this model, and has this effect on it" and the client displays that. There is no game play at all on the client - that all happens on the server.
The server decides who is where, and it is the authority on what's going on and gameplay happening. The client is just a visual representation of that. Infact, the client doesn't get any updates sent to it on objects that aren't in it's view (excepting if there is a sound playing, because then you need to have that object in the clients view to hear it) 
The client needs to be able to tell the player that someone behind them is shooting at them.

Tell the NetworkManager_Server all the models, sounds, particle effects and animations you intend to use for the level (as resource names). The system will automatically load these resources up, so they are available for use.

Set up your world, add all the Node3D objects and Node2D objects you want to have replicated over the network connection (using NetworkManager_Server.AddNodeToNetworkedNodeList to ensure that the networking knows about them)

Call NetworkManager_Server.NewGameSetup_Server, passing in how many players you have and if any of those players are on the same instance of the app as the server (ie you have a client setup too) - Note you can have any amount of players, but be aware, this server is running on your network connection. It'll flood after about 8-12.
Calling NewGameSetup_Server has the effect of building a large TCP/IP packet of all the model, sounds, animations and particle effect names you added (to ensure the client loads them too.) plus all the setup values for all the Node3D and Node2D objects you added and and told the networking about.
This TCP/IP packet is sent to each player.
On the client side, when it gets this big old packet, it's first act is to precache everything you told it you'd be using, and the second is to create all the objects in it's root world node that it knows the server has sent.
Then it sends back and Ack packet to the server saying "I got that. I'm basically ready to start playing now".

The server will wait till all players have reported in, and once they have, the game starts, and now every 20hz, the server will send an update packet to the client saying "This is where I think everything is, here are some new objects you will want to know about, and here's a list of objects that are now gone."

Then, from that point on, the NetworkManager_Client will send a packet to the server saying "Hey, I got that Frame you sent me, and here's the current status of my controller".

The server gets that input packet from the client, updates which framestate it knows that player currently has, and then does all the other good stuff of updating the main game with the new input from the player. At the end of processing that, it sends another update packet and it all begins again.

This is very top level understanding of what happens, but there's a LOT more detail to go into.

Things to know.

* You have a limit of 16384 Nodes that the networking system can know about and handle. Realistically, you aren't going to be able to update more than about 150 objects per frame anyway, because you'll burst the UDP packet size, so 16384 nodes is way overkill.
The original Quake III only had 1024, so yeah.

* We do a LOT of compression to only send that which has changed. We check position and velocity (which is not a default value in Godot, - no idea why - so we added it as an inherited class of Node3D and Node2D so it is there now, with the functionality you'd expect.

So when you are creating new Objects inside of your game, you need to be creating NetworkNode3D and NetworkNode2D, since those classes have some extra stuff in them that the networking needs.

* Another aspect of compression is the fact that if you create a new object and give it a velocity, while the networking sends all of the object details when it's created, it doesn't send any updates for position after that since the client knows where it was when it was created, and what the velocity was and will update the position itself.
It doesn't need to send the position per frame because it's updating it itself anyway. If velocity changes, it will resend position just to correct any visual drift, but generally it doesn't need much.

There are other compressions we use - only sending 16 bit floats instead of 32 bit floats. In some cases, only sending a byte that becomes one of 160 different direction vectors. While that sounds like not a lot, it's generally good enough for lots of fast moving things on the client, where an approximate representation of direction is good enough.

* When it comes to sounds being played, note, you can only play one sound on one node in one frame, in terms of the networking handling it. That said, there's nothing to stop you from playing another sound one frame later; the networking will handle that just fine and you'll end up with multiple sounds playing on your node object.

* The server can denote if the sound being played on a NetworkingNode is 3D or not and if it is 3D, it will move with the object in the world, so you get proper spatialization. You can kill a sound any time while it's still playing by just resetting it's sound index to -1 on the NetworkNode3d or NetworkNode2D.
Looping sounds are handled at the sound definition level.
Also note, if you destroy an object on the server and there is a sound playging on it, it'll kill that sound on the client too.

* When new nodes are added to the NetworkManager_Server,they need to be at the root of the scene. The networking does not cope with complicated hierarchies of nodes, because trying to transmit "This object owns that object, is the parent of this other object" will absolutely KILL your networking.
So all new nodes go in at the root (or a under a specific node) and that's how they end up on the client side.

* This is not multithreaded. The reason for that is that both the Server and the client both need to use the Node Tree (physics, organization, etc) and the Node Tree in Godot is not thread safe. If it was, we could easily thread this.
As it is, because it's not thread safe, both the client and the server run on the same thread. Not ideal, but you shouldn't be trying to make Call of Duty visuals using Godot anyway.
