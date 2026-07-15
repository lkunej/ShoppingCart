# Kubernetes Deployment Guide

This guide walks you through deploying the Auth Service and Cart Service to a Kubernetes cluster.

## Prerequisites

- A running Kubernetes cluster (minikube, kind, Docker Desktop, EKS, AKS, GKE, etc.)
- `kubectl` configured and pointing to your cluster
- Docker (for building images)
- A container registry accessible from your cluster (Docker Hub, ECR, GCR, or a local registry)
- The NGINX Ingress Controller installed in your cluster
- RSA key pair for JWT signing (`private.pem` and `public.pem`)

## Step 1: Install the NGINX Ingress Controller

If you haven't already installed the NGINX Ingress Controller:

```bash
kubectl apply -f https://raw.githubusercontent.com/kubernetes/ingress-nginx/controller-v1.10.1/deploy/static/provider/cloud/deploy.yaml
```

For minikube:

```bash
minikube addons enable ingress
```

Verify it's running:

```bash
kubectl get pods -n ingress-nginx
```

## Step 2: Build and Push Docker Images

From the repository root:

```bash
# Build Auth Service
docker build -f src/AuthService/Dockerfile -t <your-registry>/auth-service:latest .

# Build Cart Service
docker build -f src/CartService/Dockerfile -t <your-registry>/cart-service:latest .

# Push to your registry
docker push <your-registry>/auth-service:latest
docker push <your-registry>/cart-service:latest
```

If using minikube with the local Docker daemon:

```bash
eval $(minikube docker-env)
docker build -f src/AuthService/Dockerfile -t auth-service:latest .
docker build -f src/CartService/Dockerfile -t cart-service:latest .
```

If docker images built previously, just load them:  
```bash
minikube image load cart-service:latest
minikube image load auth-service:latest
```  

If using kind:

```bash
docker build -f src/AuthService/Dockerfile -t auth-service:latest .
docker build -f src/CartService/Dockerfile -t cart-service:latest .
kind load docker-image auth-service:latest
kind load docker-image cart-service:latest
```


## Step 3: Update Image References (if using a registry)

If you pushed to a remote registry, update the image field in:

- `k8s/auth-deployment.yaml` — change `image: auth-service:latest` to `image: <your-registry>/auth-service:latest`
- `k8s/cart-deployment.yaml` — change `image: cart-service:latest` to `image: <your-registry>/cart-service:latest`

If using minikube's local Docker daemon or kind with loaded images, set `imagePullPolicy: Never` on both deployments.

## Step 4: Configure Secrets

Replace all `<PLACEHOLDER>` values in the secret files with real values.

### PostgreSQL password

Edit `k8s/postgres-secrets.yaml`:

```yaml
stringData:
  POSTGRES_PASSWORD: "your-strong-password-here"
```

### RabbitMQ credentials

Create `k8s/rabbitmq-secrets.yaml` (not included in the repo):

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: rabbitmq-secrets
  labels:
    app: rabbitmq
type: Opaque
stringData:
  RABBITMQ_USERNAME: "admin"
  RABBITMQ_PASSWORD: "your-rabbitmq-password"
```

### Auth Service secrets

Edit `k8s/auth-secrets.yaml`:

```yaml
stringData:
  ConnectionStrings__DefaultConnection: "Host=postgres-service;Database=auth_db;Username=postgres;Password=your-strong-password-here"
  Jwt__PrivateKey: "<contents of private.pem>"
  Jwt__PublicKey: "<contents of public.pem>"
  RabbitMQ__Username: "admin"
  RabbitMQ__Password: "your-rabbitmq-password"
```

### Cart Service secrets

Edit `k8s/cart-secrets.yaml`:

```yaml
stringData:
  ConnectionStrings__DefaultConnection: "Host=postgres-service;Database=cart_db;Username=postgres;Password=your-strong-password-here"
  Jwt__PublicKey: "<contents of public.pem>"
  RabbitMQ__Username: "admin"
  RabbitMQ__Password: "your-rabbitmq-password"
  PaymentService__ApiKey: "your-payment-api-key"
  PaymentService__MerchantId: "your-merchant-id"
  FiscalService__CertificatePassword: "your-cert-password"
  FiscalService__BusinessOib: "your-business-oib"
```

### Fiscal certificate

Encode your PFX file and update `k8s/fiscal-cert-secret.yaml`:

```bash
# Linux/macOS
cat fiskal.pfx | base64 -w 0

# If no actual fiskal.pfx file, mock this by doing:
echo "test" | base64 -w 0

# Windows PowerShell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("fiskal.pfx"))
```
Paste the output as the value of `fiskal.pfx` in the secret file.

### TLS certificate (for Ingress)

Create a TLS secret for HTTPS termination:

```bash
kubectl create secret tls tls-secret \
  --cert=path/to/tls.crt \
  --key=path/to/tls.key
```

For local development, generate a self-signed cert:

```bash
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout tls.key -out tls.crt \
  -subj "/CN=api.example.com"

kubectl create secret tls tls-secret --cert=tls.crt --key=tls.key
```

## Step 5: Deploy the Data Layer

Apply in this order — the data layer must be running before the application services start:

```bash
# Apply all data layer resources (secrets, services, statefulsets)
kubectl apply -f k8s/data-layer/
```

Wait until all pods are ready:

```bash
kubectl wait --for=condition=ready pod -l app=postgres --timeout=120s
kubectl wait --for=condition=ready pod -l app=redis --timeout=60s
kubectl wait --for=condition=ready pod -l app=rabbitmq --timeout=90s
```

## Step 6: Deploy Application Services

```bash
# Auth Service (all resources in one command)
kubectl apply -f k8s/auth-service/

# Cart Service (all resources in one command)
kubectl apply -f k8s/cart-service/
```

Wait for the services to come up:

```bash
kubectl wait --for=condition=ready pod -l app=auth-service --timeout=120s
kubectl wait --for=condition=ready pod -l app=cart-service --timeout=120s
```

## Step 7: Deploy the Ingress

```bash
kubectl apply -f k8s/ingress/
```

## Step 8: Verify the Deployment

Check all pods are running:

```bash
kubectl get pods
```

Expected output (all should show `Running` / `Ready`):

```
NAME                            READY   STATUS    RESTARTS   AGE
auth-service-xxxxx-yyyyy        1/1     Running   0          2m
auth-service-xxxxx-zzzzz        1/1     Running   0          2m
cart-service-xxxxx-yyyyy        1/1     Running   0          2m
cart-service-xxxxx-zzzzz        1/1     Running   0          2m
postgres-0                      1/1     Running   0          5m
redis-0                         1/1     Running   0          5m
rabbitmq-0                      1/1     Running   0          5m
default-backend-xxxxx-yyyyy     1/1     Running   0          1m
```

Check services:

```bash
kubectl get svc
```

Check ingress:

```bash
kubectl get ingress
```

Test the health endpoints (port-forward for quick check):

```bash
kubectl port-forward svc/auth-service 8080:80
# In another terminal:
curl http://localhost:8080/health
```

If using minikube, get the ingress IP:

```bash
minikube ip
# Then curl with the Host header:
curl -k --resolve api.example.com:443:$(minikube ip) https://api.example.com/auth/login
```

## Useful Commands

```bash
# View logs for a service
kubectl logs -l app=auth-service --tail=50 -f
kubectl logs -l app=cart-service --tail=50 -f

# Describe a pod (for debugging startup issues)
kubectl describe pod <pod-name>

# Check HPA status
kubectl get hpa

# Check events (useful for debugging)
kubectl get events --sort-by=.metadata.creationTimestamp
```

---

## Tearing Down

### Remove application services and ingress

```bash
kubectl delete -f k8s/ingress/
kubectl delete -f k8s/auth-service/
kubectl delete -f k8s/cart-service/
```

### Remove config and secrets

```bash
kubectl delete secret tls-secret
```

### Remove data layer (destroys all data)

```bash
kubectl delete -f k8s/data-layer/
```

### Delete Persistent Volume Claims (permanent data loss)

StatefulSets don't delete PVCs automatically. To fully clean up:

```bash
kubectl delete pvc postgres-data-postgres-0
kubectl delete pvc redis-data-redis-0
kubectl delete pvc rabbitmq-data-rabbitmq-0
```

### One-liner: delete everything

If you want to tear down the entire stack in one shot:

```bash
kubectl delete -f k8s/ingress/ -f k8s/auth-service/ -f k8s/cart-service/ -f k8s/data-layer/
kubectl delete secret tls-secret
kubectl delete pvc postgres-data-postgres-0 redis-data-redis-0 rabbitmq-data-rabbitmq-0
```

> **Warning:** The one-liner deletes all data. PVCs hold your PostgreSQL databases, Redis cache, and RabbitMQ state. Only use this if you're OK losing everything.
