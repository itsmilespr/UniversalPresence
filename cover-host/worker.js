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

      const declaredType = (request.headers.get("content-type") || "").split(";")[0].trim();
      let body;
      let type;

      if (declaredType.startsWith("image/")) {
        // Raw image body: the desktop app sends the cover bytes directly.
        type = declaredType;
        body = await request.arrayBuffer();
      } else {
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

        type = file.type || "image/jpeg";
        body = await file.arrayBuffer();
      }

      if (!type.startsWith("image/")) {
        return new Response("Not an image", { status: 415 });
      }

      if (body.byteLength === 0 || body.byteLength > 10 * 1024 * 1024) {
        return new Response("File too large", { status: 413 });
      }

      // Validación por magic bytes, no solo por Content-Type.
      const bytes = new Uint8Array(body);
      const magicOk =
        (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) || // JPEG
        (bytes.length >= 8 && bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) || // PNG
        (bytes.length >= 6 && bytes[0] === 0x47 && bytes[1] === 0x49 && bytes[2] === 0x46) || // GIF
        (bytes.length >= 12 && bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46 && bytes[8] === 0x57 && bytes[9] === 0x45 && bytes[10] === 0x42 && bytes[11] === 0x50); // WEBP
      if (!magicOk) {
        return new Response("Invalid image", { status: 415 });
      }

      const extension =
        type.includes("png") ? "png" :
        type.includes("webp") ? "webp" :
        type.includes("gif") ? "gif" : "jpg";

      const key = `${crypto.randomUUID()}.${extension}`;
      await env.FILES.put(key, body, {
        httpMetadata: { contentType: type },
      });

      try {
        const listed = await env.FILES.list({ prefix: "covers/", limit: 1000 });
        if (listed.objects.length >= 1000) {
          const oldest = listed.objects
            .slice()
            .sort((a, b) => (a.uploaded?.getTime?.() ?? 0) - (b.uploaded?.getTime?.() ?? 0))
            .slice(0, 100);
          await Promise.all(oldest.map((o) => env.FILES.delete(o.key)));
        }
      } catch {}

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
