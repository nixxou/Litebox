// One row of Parents.xml, handed to plugins through IDataManager.GetAllParents().
//
// LiteBox reads that file to build its own sidebar and then dropped it on the floor: GetAllParents answered
// with an empty array, which tells a plugin the library is flat — no platform inside a category, no playlist
// nested under a platform. The tree it was describing was right there in memory.
//
// A plain record of what the file says. The names are the file's own, and the resolved node is deliberately
// left null: a plugin that wants the object looks it up by the name it finds here, exactly as it would under
// LaunchBox, and nothing here has to stay in sync with the catalog.

#nullable enable

namespace LbApiHost.Host.Data;

internal sealed class HostParent : LbApiHost.Generated.DummyParent
{
}
