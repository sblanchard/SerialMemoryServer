import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 500,
  duration: '30s',
  thresholds: {
    http_req_duration: ['p(95)<50'],
  },
};

export default function () {
  const payload = JSON.stringify({ score: Math.random() * 100 });
  const res = http.post('http://localhost:5000/context/game', payload, {
    headers: { 'Content-Type': 'application/json' },
  });
  check(res, { 'status 200': (r) => r.status === 200 });
  sleep(0.1);
}
