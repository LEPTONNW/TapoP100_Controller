외부제어 사용방법
  
  
  fetch("http://내아이피:8080/p100_on", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      tapo_ip: "192.168.0.xxx",
      id: "example@exam.com",
      pass: "1234"
    })
  })
    .then(r => r.text())
    .then(console.log);

  fetch("http://내아이피:8080/p100_off", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      tapo_ip: "192.168.0.xxx",
      id: "example@exam.com",
      pass: "1234"
    })
  })
