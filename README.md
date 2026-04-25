# AI Recruitment Chatbot System

## Overview
A full-stack AI-powered recruitment platform designed to automate candidate screening and simulate real interview environments. The system combines chatbot interaction, behavioural monitoring, and admin analytics to improve hiring efficiency.

Developed as a real-world hiring solution, this system enables companies to conduct automated interviews while tracking candidate behaviour and performance.

---

## Key Features

### 🤖 AI Chatbot Interface
- Interactive chatbot for job applications
- Dynamic job listing and selection flow
- Resume upload and candidate onboarding
- Structured interview conversation handling

### 🛡️ Candidate Monitoring
- Tab switching detection
- Copy-paste tracking
- Behavioural logging during interview sessions
- Face detection support using OpenCV

### 📊 Admin Dashboard
- View all candidates and applications
- Track interview status (Submitted / In Progress / Not Started)
- Resume and interview data management
- Export data to Excel
- Email / WhatsApp tracking system

### 🎯 Interview Evaluation
- Resume scoring (AI-based logic)
- Interview scoring system
- Personality assessment metrics:
  - Teamwork
  - Stress Management
  - Leadership
  - Detail Orientation

---

## 🧱 Tech Stack

**Backend**
- ASP.NET Core MVC
- C#

**Frontend**
- HTML, CSS, JavaScript
- Bootstrap (CDN)

**AI / ML**
- Chatbot logic (rule-based / API integrated)
- OpenCV Haar Cascade (face detection)
- Python integration (`scraper.py`)

**Database**
- SQL Server / Local DB

---

## 🏗️ Architecture

- **Controllers** → Handle requests and chatbot flow  
- **Services** → Business logic (chat, notifications, scraping)  
- **Models** → Candidate + interview data  
- **Views** → Chat UI + Admin panel  
- **Python Layer** → Data scraping & external processing  

---

## 📸 Screenshots

### 🧠 Chatbot Interface
![Chatbot UI](Screenshot%202025-12-04%20192600.png)

### 💬 Interview Conversation Flow
![Conversation](Screenshot%202025-12-04%20192657.png)

### 📊 Candidate Evaluation Dashboard
![Evaluation](Screenshot%202025-12-04%20192918.png)

### 📋 Admin Panel – Candidate Management
![Admin Table](Screenshot%202025-12-04%20192944.png)

### 📈 Reporting & Filters
![Reports](Screenshot%202025-12-04%20193041.png)

### 📊 Analytics Dashboard
![Dashboard](Screenshot%202025-12-04%20193115.png)

---

## 🚀 How to Run

1. Clone the repository  
2. Open `.sln` file in Visual Studio  
3. Configure `appsettings.json` if required  
4. Run the application  

---

## 💡 Use Case

- Automates candidate screening process  
- Enables remote interview management  
- Tracks candidate behaviour during assessments  
- Reduces manual hiring workload  

---

## 🔥 Highlights

- Built a real-world recruitment system from scratch  
- Integrated chatbot + monitoring + admin analytics  
- Combined .NET backend with Python components  
- Designed scalable and modular architecture  

---

## ⚙️ Future Improvements

- Integrate advanced LLM APIs (GPT / Gemini)  
- Real-time interview analytics dashboard  
- Cloud deployment (Azure / AWS)  
- Enhanced face tracking & proctoring  

---

## 👨‍💻 Author

**Shahid Saiyed**  
MSc Artificial Intelligence for Business – London  
Backend Developer | AI Systems Enthusiast  

---

## 📬 Contact

- LinkedIn: https://linkedin.com/in/shahid-saiyed  
- Email: saiyedshahid9999@gmail.com
