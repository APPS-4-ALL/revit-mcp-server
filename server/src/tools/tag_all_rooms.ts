import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

export function registerTagAllRoomsTool(server: McpServer) {
  server.tool(
    "tag_all_rooms",
    `Tag rooms with room tags. Rooms already tagged in the target view are skipped, so re-running is safe.

GUIDANCE:
- "Tag all rooms" / "tag every room" (whole building): set allLevels=true. Tags the main floor plan of every level that has rooms, without changing the active view.
- Default (allLevels false): tags the rooms in the active floor plan only.`,
    {
      allLevels: z
        .boolean()
        .optional()
        .describe("Tag rooms on every level, each in that level's main floor plan. Default: false (active plan only)."),
      useLeader: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to use a leader line when creating the tags"),
      tagTypeId: z
        .string()
        .optional()
        .describe("The ID of the specific room tag family type to use."),
      roomIds: z
        .array(z.number())
        .optional()
        .describe("Optional array of specific room element IDs to tag."),
    },
    async (args, extra) => {
      const params = { ...args, allLevels: args.allLevels ?? false };
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("tag_rooms", params);
        });

        return rawToolResponse("tag_all_rooms", response);
      } catch (error) {
        return rawToolError("tag_all_rooms", `Room tagging failed: ${
                errorMessage(error)
              }`);
      }
    }
  );
}
