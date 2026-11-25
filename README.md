# Client Integry DT

Host Windows de ejecución de gadgets, C# / .NET 9.

## Fase 5
- enrolamiento y credencial por dispositivo;
- WebSocket persistente + heartbeat;
- recepción de Jobs genéricos;
- descarga autenticada del paquete ZIP desde Backend;
- caché por `gadget/version`;
- verificación SHA-256 del ZIP;
- verificación RSA-PSS con clave pública de Integry DT;
- validación de `manifest.json` (id, versión, protocolo y plataforma);
- extracción a runtime separado;
- ejecución del `entrypoint` como proceso independiente;
- contrato JSON por stdin y resultado JSON por stdout;
- timeout de ejecución;
- reporte de estados/resultados al Backend.

El Client no contiene la clave privada de firma. Durante desarrollo la clave pública está en `C:\ProgramData\IntegryDT\Keys\gadget-public.pem`.
