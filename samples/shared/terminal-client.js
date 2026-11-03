/* Shared raw-WebSocket terminal transport. The caller owns xterm and its presentation. */
window.ReplTerminalClient = {
  connect({ terminal, url, hello, onOpen, onClose, onOutput, reportResize = true }) {
    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    const decoder = new TextDecoder();
    const send = text => { if (socket.readyState === WebSocket.OPEN) socket.send(text); };
    const input = terminal.onData(send);
    const resize = terminal.onResize(({ cols, rows }) => {
      if (!reportResize) return;
      send(`@@repl:resize ${JSON.stringify({ cols, rows })}`);
      send(`\x1b[8;${rows};${cols}t`);
    });
    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      input.dispose();
      resize.dispose();
      onClose?.();
    };
    socket.onmessage = event => onOutput(typeof event.data === 'string' ? event.data : decoder.decode(event.data, { stream: true }));
    socket.onopen = () => {
      send(hello);
      if (reportResize) send(`\x1b[8;${terminal.rows};${terminal.cols}t`);
      onOpen?.();
    };
    socket.onclose = close;
    socket.onerror = () => { socket.close(); close(); };
    return { send, dispose() { socket.close(); close(); } };
  }
};
