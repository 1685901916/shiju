"""Local-only integration fixture. No external requests or real API credentials."""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=18746)
parser.add_argument('--log', type=Path, required=True)
args = parser.parse_args()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        messages = request['messages']
        # Record protocol data for the controlled fixture; never record the Authorization header.
        with args.log.open('a', encoding='utf-8') as stream:
            stream.write(json.dumps({'path': self.path, 'messages': messages, 'stream': request.get('stream')}, ensure_ascii=False) + '\n')
        if self.headers.get('Authorization') != 'Bearer local-test-key':
            self.send_response(401)
            self.end_headers()
            return
        if len(messages) == 2 and 'Chinese–English' in messages[0]['content']:
            text = ('The best way to learn a language is to use it every day to say what you really think.\n【语块】\n'
                    'The best way to ... is to ... | ……最好的办法是……\nsay what you really think | 说出你真实的想法')
        elif len(messages) == 2:
            text = ('说到底，最难的不是开始，而是坚持下去。\n【语块】\n'
                    'at the end of the day | 说到底；归根结底\nstick with it | 坚持下去')
        else:
            text = f'【本机接口验证】收到你的问题：{messages[-1]["content"]}\n当前包含 {len(messages)} 条上下文消息；原文和英文译文已随请求传入。\nThe best way to learn… 表示“学习……的最佳方式”。'
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
        self.send_header('Cache-Control', 'no-cache')
        self.end_headers()
        try:
            for i in range(0, len(text), 5):
                event = {'choices': [{'delta': {'content': text[i:i+5]}, 'finish_reason': None}]}
                self.wfile.write(('data: ' + json.dumps(event, ensure_ascii=False) + '\n\n').encode())
                self.wfile.flush()
                time.sleep(0.025)
            self.wfile.write(b'data: [DONE]\n\n')
        except (BrokenPipeError, ConnectionResetError):
            pass


args.log.parent.mkdir(parents=True, exist_ok=True)
print(f'Local SSE fixture listening on 127.0.0.1:{args.port}', flush=True)
ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
