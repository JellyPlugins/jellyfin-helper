/** Removes only the recommendation playlists belonging to a test-created account before deleting that account. */
interface FixtureResponse {
  ok(): boolean;
  status(): number;
  json(): Promise<unknown>;
}

interface FixturePlaylistClient {
  get(url: string): Promise<FixtureResponse>;
  delete(url: string): Promise<FixtureResponse>;
}

const ITEM_ID = /^(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i;

export async function removeFixtureRecommendationPlaylists(ctx: FixturePlaylistClient, userId: string, userName: string): Promise<number> {
  if (!ITEM_ID.test(userId) || !userName.trim()) throw new Error('Fixture playlist cleanup requires an explicit account identity');
  const response = await ctx.get(`/Items?IncludeItemTypes=Playlist&Recursive=true&userId=${encodeURIComponent(userId)}`);
  if (!response.ok()) throw new Error(`Fixture playlist lookup failed: ${response.status()}`);
  const body = await response.json() as { Items?: Array<{ Id?: string; Name?: string }> };
  if (!Array.isArray(body.Items)) throw new Error('Fixture playlist lookup did not return an Items array');
  const expectedName = '🎬 Recommended for ' + userName;
  const managed = body.Items.filter(item => item.Name === expectedName
    || (item.Name?.startsWith(expectedName) && /^\d+$/.test(item.Name.slice(expectedName.length))));
  for (const item of managed) {
    if (!item.Id || !ITEM_ID.test(item.Id)) throw new Error('Fixture playlist has an invalid item identity');
    const deleted = await ctx.delete(`/Items/${item.Id}`);
    if (!deleted.ok()) throw new Error(`Fixture playlist deletion failed for ${item.Id}: ${deleted.status()}`);
  }
  return managed.length;
}
