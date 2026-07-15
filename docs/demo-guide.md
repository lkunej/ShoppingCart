# Demo Guide: K8s Auth & Cart Services

A step-by-step script for demonstrating the full Kubernetes-deployed microservices stack.

## Prerequisites

- minikube installed
- kubectl installed
- Docker images `auth-service:latest` and `cart-service:latest` loaded into minikube
- All secret YAML files in `k8s-local-deployment/` populated with real values

---

## Part 1: Start the Kubernetes Stack

### 1.1 Start minikube

```bash
minikube start
```

### 1.2 Enable the Ingress addon

```bash
minikube addons enable ingress
```

Wait for the ingress controller to be ready:

```bash
kubectl wait --namespace ingress-nginx \
  --for=condition=ready pod \
  --selector=app.kubernetes.io/component=controller \
  --timeout=120s
```

### 1.3 Load Docker images into minikube (if not already loaded)

```bash
minikube image load auth-service:latest
minikube image load cart-service:latest
```

### 1.4 Deploy the data layer

```bash
kubectl apply -f k8s-local-deployment/data-layer/
```

Wait for all data pods to be ready:

```bash
kubectl wait --for=condition=ready pod -l app=postgres --timeout=120s
kubectl wait --for=condition=ready pod -l app=redis --timeout=60s
kubectl wait --for=condition=ready pod -l app=rabbitmq --timeout=90s
```

### 1.5 Deploy application services

```bash
kubectl apply -f k8s-local-deployment/auth-service/
kubectl apply -f k8s-local-deployment/cart-service/
```

Wait for services to be ready:

```bash
kubectl wait --for=condition=ready pod -l app=auth-service --timeout=120s
kubectl wait --for=condition=ready pod -l app=cart-service --timeout=120s
```

### 1.6 Deploy the ingress

```bash
kubectl apply -f k8s-local-deployment/ingress/
```

### 1.7 Verify everything is running

```bash
kubectl get pods -A
```

All pods should show `Running` and `1/1 Ready`.

---

## Part 2: Access via Ingress (Infrastructure Demo)

### 2.1 Open ingress tunnel

In a **separate terminal**, run:

```bash
minikube service ingress-nginx-controller -n ingress-nginx
```

This prints URLs like:

```
| ingress-nginx | ingress-nginx-controller | http/80  | http://127.0.0.1:XXXXX |
| ingress-nginx | ingress-nginx-controller | https/443| http://127.0.0.1:YYYYY |
```

Note the **HTTPS port** (YYYYY). All curl commands below use this port.

Alternatively, use the minikube IP directly:

```bash
# Get IP and NodePort
minikube ip                    # e.g. 192.168.49.2
kubectl get svc -n ingress-nginx ingress-nginx-controller
# Look for 443:XXXXX/TCP     # e.g. 30256
```

Then use `https://<minikube-ip>:<NodePort>` in all curl commands.

### 2.2 Test TLS connection

```bash
curl -k -v https://192.168.49.2:30256/auth/health
```

**What to show:** The verbose output confirms TLS 1.3 handshake:
```
* SSL connection using TLSv1.3 / TLS_AES_256_GCM_SHA384
```

### 2.3 Register a user (through ingress → auth-service)

```bash
curl -k -X POST https://192.168.49.2:30256/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@example.com","password":"SecureP@ss123","role":"Customer"}'
```

**Expected:** 200 OK with user details.

### 2.4 Login (through ingress → auth-service)

```bash
curl -k -X POST https://192.168.49.2:30256/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@example.com","password":"SecureP@ss123"}'
```

**Expected:** 200 OK with JWT token pair:
```json
{
  "accessToken": "eyJhbG...",
  "refreshToken": "...",
  "expiresIn": 900
}
```

Save the access token:
```bash
TOKEN="eyJhbG..."
```

### 2.5 Access cart WITHOUT token (shows auth subrequest blocking)

```bash
curl -k https://192.168.49.2:30256/api/cart
```

**Expected:** 401 Unauthorized. The ingress called `/auth/validate`, it failed (no token), and blocked the request from reaching cart-service.

### 2.6 Access cart WITH token (shows full routing flow)

```bash
curl -k https://192.168.49.2:30256/api/cart \
  -H "Authorization: Bearer $TOKEN"
```

**Expected:** 200 OK with empty cart:
```json
{"items":[], "totalPrice":{"amount":0,"currency":"EUR"}}
```

**Flow:** Client → Ingress (TLS termination) → auth-service `/auth/validate` (token valid) → cart-service `GET /api/cart`

### 2.7 Add item to cart (through ingress → auth → cart-service)

```bash
curl -k -X POST https://192.168.49.2:30256/api/cart/items \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"productId":"11111111-1111-1111-1111-111111111111","quantity":2}'
```

### 2.8 Retrieve updated cart

```bash
curl -k https://192.168.49.2:30256/api/cart \
  -H "Authorization: Bearer $TOKEN"
```

### 2.9 Refresh token (public route, no auth subrequest)

```bash
curl -k -X POST https://192.168.49.2:30256/auth/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<paste-refresh-token-here>"}'
```

### 2.10 Test rate limiting (optional)

Hit the login endpoint rapidly to trigger the 429:

```bash
for i in $(seq 1 10); do
  curl -k -s -o /dev/null -w "%{http_code}\n" \
    -X POST https://192.168.49.2:30256/auth/login \
    -H "Content-Type: application/json" \
    -d '{"email":"demo@example.com","password":"wrongpassword"}'
done
```

After 5 failures, responses should switch to `429`.

---

## Part 3: Swagger UI (API Demo)

### 3.1 Port-forward Auth Service

In a **separate terminal**:

```bash
kubectl port-forward svc/auth-service 5001:80
```

Open in browser: **http://localhost:5001/swagger**

### 3.2 Port-forward Cart Service

In another **separate terminal**:

```bash
kubectl port-forward svc/cart-service 5002:80
```

Open in browser: **http://localhost:5002/swagger**

### 3.3 Demo flow in Swagger

1. **Auth Swagger (localhost:5001/swagger):**
   - POST `/auth/register` — create a user
   - POST `/auth/login` — get a JWT token
   - Copy the `accessToken` value

2. **Cart Swagger (localhost:5002/swagger):**
   - Click "Authorize" button (lock icon at top)
   - Paste the token as `Bearer <token>`
   - GET `/api/cart` — shows empty cart
   - POST `/api/cart/items` — add an item
   - GET `/api/cart` — shows updated cart with total

---

## Part 4: Show the Infrastructure

### 4.1 Show running pods

```bash
kubectl get pods
```

### 4.2 Show services and networking

```bash
kubectl get svc
kubectl get ingress
```

### 4.3 Show HPA configuration

```bash
kubectl get hpa
```

### 4.4 Show health endpoints

```bash
curl -k https://192.168.49.2:30256/auth/health
```

### 4.5 Show logs (structured JSON with correlation IDs)

```bash
kubectl logs -l app=auth-service --tail=10
kubectl logs -l app=cart-service --tail=10
```

### 4.6 Show pod scaling

```bash
kubectl scale deployment auth-service --replicas=4
kubectl get pods -l app=auth-service -w
# Then scale back
kubectl scale deployment auth-service --replicas=2
```

---

## Part 5: Teardown

```bash
kubectl delete -f k8s-local-deployment/ingress/
kubectl delete -f k8s-local-deployment/auth-service/
kubectl delete -f k8s-local-deployment/cart-service/
kubectl delete -f k8s-local-deployment/data-layer/
kubectl delete pvc --all
minikube stop
```

---

## Quick Reference: Ports

| Service | Port-Forward | Purpose |
|---------|-------------|---------|
| Auth Service Swagger | localhost:5001 | API docs UI |
| Cart Service Swagger | localhost:5002 | API docs UI |
| Ingress (HTTPS) | minikube-ip:NodePort | Full infrastructure flow |

## Quick Reference: Demo Flow Summary

```
1. Register user     → POST /auth/register (public)
2. Login             → POST /auth/login (public) → get JWT
3. Cart without JWT  → GET /api/cart → 401 (blocked by ingress)
4. Cart with JWT     → GET /api/cart → 200 (ingress validates, forwards)
5. Add to cart       → POST /api/cart/items → item added
6. Show health       → GET /auth/health → service + dependency status
7. Show logs         → kubectl logs → structured JSON, correlation IDs
8. Scale pods        → kubectl scale → replicas change instantly
```
