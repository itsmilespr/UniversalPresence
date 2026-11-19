export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname.startsWith("/covers/")) {
      const key = decodeURIComponent(url.pathname.slice("/covers/".length));
      if (!key || key.includes("..")) {
        return new Response("Not found", { status: 404 });
      }

      const object = await env.FILES.get(key);
      if (!object) {
        return new Response("Not found", { status: 404 });
      }

      const headers = new Headers();
      object.writeHttpMetadata(headers);
      headers.set("Cache-Control", "public, max-age=31536000, immutable");
      headers.set("Access-Control-Allow-Origin", "*");
      return new Response(object.body, { headers });
    }

    if (request.method === "POST" && url.pathname === "/upload") {
      const token = request.headers.get("X-Upload-Token");
      if (!env.UPLOAD_TOKEN || !token || token !== env.UPLOAD_TOKEN) {
        return new Response("Unauthorized", { status: 401 });
      }

      let form;
      try {
        form = await request.formData();
      } catch {
        return new Response("Invalid form data", { status: 400 });
      }

      const file = form.get("file");
      if (!file || typeof file === "string") {
        return new Response("Missing file", { status: 400 });
      }

      if (file.size > 10 * 1024 * 1024) {
        return new Response("File too large", { status: 413 });
      }

      const type = file.type || "image/jpeg";
      if (!type.startsWith("image/")) {
        return new Response("Not an image", { status: 415 });
      }

      const extension =
        type.includes("png") ? "png" :
        type.includes("webp") ? "webp" :
        type.includes("gif") ? "gif" : "jpg";

      const key = `${crypto.randomUUID()}.${extension}`;
      await env.FILES.put(key, file.stream(), {
        httpMetadata: { contentType: type },
      });

      return Response.json({ url: `${url.origin}/covers/${key}` });
    }

    if (request.method === "OPTIONS") {
      return new Response(null, {
        headers: {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
          "Access-Control-Allow-Headers": "X-Upload-Token, Content-Type",
        },
      });
    }

    return new Response("Not found", { status: 404 });
  },
};
