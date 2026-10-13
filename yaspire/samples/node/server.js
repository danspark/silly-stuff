const http = require("node:http");

const port = process.env.PORT ?? 3000;
const message = process.env.MESSAGE ?? "Hello";

http
  .createServer((_, response) => {
    response.writeHead(200, { "content-type": "text/plain" });
    response.end(`${message}, served by Node ${process.version}\n`);
  })
  .listen(port, () => console.log(`Listening on ${port}`));
